using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace InfoMail.Services;

public sealed class SharePointRuleSource(HttpClient http, TokenCredential credential)
{
    public const string SiteId = "blindesamfund.sharepoint.com,0224fb1e-b25c-41f7-93d5-22e84b601907,aae5b9ae-72ee-4acd-85f3-d3ed176084b3";

    public async Task<WordRuleSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/sites/{SiteId}/drive/root:/regeltabel.docx:/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Found)
        {
            var location = response.Headers.Location;
            if (location is null || !location.IsAbsoluteUri || location.Scheme != "https" ||
                !location.Host.Equals("blindesamfund.sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
                location.UserInfo.Length != 0 || location.Port != 443)
            {
                throw new InvalidDataException("Unexpected SharePoint download redirect.");
            }
            // Download links are preauthorized. Never forward the Graph bearer token.
            using var download = await http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            download.EnsureSuccessStatusCode();
            return await ParseAsync(download.Content, cancellationToken);
        }
        response.EnsureSuccessStatusCode();
        return await ParseAsync(response.Content, cancellationToken);
    }

    private static async Task<WordRuleSnapshot> ParseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumBytes = 8 * 1024 * 1024;
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + length > maximumBytes)
            {
                throw new InvalidDataException("SharePoint Word file exceeds the download size limit.");
            }
            await memory.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
        }
        memory.Position = 0;
        return WordRuleTable.Read(memory);
    }
}

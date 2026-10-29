using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace InfoMail.Services;

public sealed class ShadowSourceInventory(HttpClient http, TokenCredential credential)
{
    private const string Site = "https://graph.microsoft.com/v1.0/sites/" + SharePointRuleSource.SiteId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await PrintAsync(Site + "/drive/root/children", "Documents", cancellationToken);
        using var lists = await ReadAsync(Site + "/lists", cancellationToken);
        var matches = lists.RootElement.GetProperty("value").EnumerateArray()
            .Where(item => item.GetProperty("webUrl").GetString()?
                .Contains("/Lists/MailRouting", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("Exactly one MailRouting list is required.");
        var id = matches[0].GetProperty("id").GetString();
        Console.WriteLine("MailRouting list ID=" + id);
        await PrintAsync(Site + "/lists/" + id + "/columns", "Columns", cancellationToken);
        using var items = await ReadAsync(Site + "/lists/" + id + "/items?$expand=fields&$top=3", cancellationToken);
        foreach (var item in items.RootElement.GetProperty("value").EnumerateArray())
        {
            Console.WriteLine("MailRouting field names: " +
                string.Join(", ", item.GetProperty("fields").EnumerateObject().Select(field => field.Name)));
        }
    }

    private async Task PrintAsync(string url, string label, CancellationToken cancellationToken)
    {
        for (var page = 0; page < 20; page++)
        {
            using var document = await ReadAsync(url, cancellationToken);
            foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
            {
                Console.WriteLine(label + ": " + JsonSerializer.Serialize(new
                {
                    Id = item.GetProperty("id").GetString(),
                    Name = item.GetProperty("name").GetString(),
                    Type = item.TryGetProperty("folder", out _) ? "Folder" :
                        item.TryGetProperty("file", out _) ? "File" :
                        item.TryGetProperty("text", out _) ? "TextColumn" :
                        item.TryGetProperty("choice", out _) ? "ChoiceColumn" : "Other",
                    Description = label == "Columns" && item.TryGetProperty("description", out var description)
                        ? description.GetString() : null
                }));
            }
            if (!document.RootElement.TryGetProperty("@odata.nextLink", out var next))
                return;
            url = next.GetString() ?? throw new InvalidDataException("Inventory next page missing.");
        }
        throw new InvalidDataException("Source inventory exceeds page limit.");
    }

    private async Task<JsonDocument> ReadAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "graph.microsoft.com" || !url.StartsWith(Site + "/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected source inventory URL.");
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed class GraphProcessingMailbox(HttpClient http, TokenCredential credential) : IProcessingMailbox
{
    private const string Root = "https://graph.microsoft.com/v1.0/users/info%40blind.dk";

    public async IAsyncEnumerable<EmailMessage> ReadInboxAsync(DateTimeOffset start,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        var filter = Uri.EscapeDataString($"receivedDateTime ge {start:O} and receivedDateTime le {end:O}");
        string? next = $"{Root}/mailFolders/inbox/messages?$top=50&$select=id,subject,body,receivedDateTime,from,internetMessageHeaders&$orderby=receivedDateTime%20asc&$filter={filter}";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!visited.Add(next))
            {
                throw new InvalidDataException("Graph repeated a pagination link.");
            }
            using var response = await GetAsync(next, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            foreach (var value in document.RootElement.GetProperty("value").EnumerateArray())
            {
                var body = value.GetProperty("body");
                var id = value.GetProperty("id").GetString();
                var received = value.GetProperty("receivedDateTime").GetDateTimeOffset();
                if (string.IsNullOrWhiteSpace(id) || received < start || received > end ||
                    !string.Equals(body.GetProperty("contentType").GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Graph returned an invalid message or activation range.");
                }
                var headers = value.GetProperty("internetMessageHeaders");
                yield return new EmailMessage
                {
                    Id = id, ReceivedAt = received,
                    Subject = value.GetProperty("subject").GetString() ?? "",
                    Body = body.GetProperty("content").GetString() ?? "",
                    From = value.GetProperty("from").GetProperty("emailAddress").GetProperty("address").GetString() ?? "",
                    IsAutomatic = headers.EnumerateArray().Any(header =>
                    {
                        var name = header.GetProperty("name").GetString();
                        var text = header.GetProperty("value").GetString();
                        return (string.Equals(name, "Auto-Submitted", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(text?.Trim(), "no", StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(name, "X-Loop", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "X-Auto-Response-Suppress", StringComparison.OrdinalIgnoreCase);
                    })
                };
            }
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    public async Task ForwardAsync(string id, string[] to, string[] cc, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Post, $"{Root}/messages/{Uri.EscapeDataString(id)}/forward", cancellationToken);
        request.Content = JsonContent.Create(new
        {
            comment = $"[InfoMail-Forward:{MailProcessingEngine.Reference(id)}]",
            message = new
            {
                toRecipients = to.Select(address => new { emailAddress = new { address } }),
                ccRecipients = cc.Select(address => new { emailAddress = new { address } })
            }
        });
        // No write retries: timeout/5xx may still mean Graph accepted the forward.
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new HttpRequestException("Forward did not return the required 202.", null, response.StatusCode);
        }
    }

    public Task<string> FindDestinationAsync(CancellationToken cancellationToken) =>
        FindDestinationAsync("Videresendt via regler", cancellationToken);

    public async Task<string> FindDestinationAsync(string folderName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderName) || folderName.Length > 255 ||
            folderName.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
        {
            throw new InvalidDataException("Invalid destination folder name.");
        }
        string? next = $"{Root}/mailFolders/inbox/childFolders?$top=100&$select=id,displayName";
        var matches = new List<string>();
        var visited = new HashSet<string>();
        while (next is not null)
        {
            if (!visited.Add(next))
            {
                throw new InvalidDataException("Folder pagination cycle.");
            }
            using var response = await GetAsync(next, cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            foreach (var folder in document.RootElement.GetProperty("value").EnumerateArray())
            {
                if (folder.GetProperty("displayName").GetString() == folderName)
                {
                    matches.Add(folder.GetProperty("id").GetString() ?? throw new InvalidDataException("Folder ID missing."));
                }
            }
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return matches.Count == 1 && !string.IsNullOrWhiteSpace(matches[0])
            ? matches[0] : throw new InvalidDataException($"Exactly one existing destination folder named '{folderName}' directly under Inbox is required.");
    }

    public async Task MoveAsync(string id, string destination, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Post, $"{Root}/messages/{Uri.EscapeDataString(id)}/move", cancellationToken);
        request.Content = JsonContent.Create(new { destinationId = destination });
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new HttpRequestException("Move did not return the required 201.", null, response.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = await RequestAsync(HttpMethod.Get, url, cancellationToken);
            var response = await http.SendAsync(request, cancellationToken);
            if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
            {
                var delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2 << attempt));
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }
                if (delay > TimeSpan.FromMinutes(1))
                {
                    response.Dispose();
                    throw new HttpRequestException("Graph requested a longer retry interval; stop this scan.", null, HttpStatusCode.TooManyRequests);
                }
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Host != "graph.microsoft.com" ||
            !uri.AbsolutePath.StartsWith("/v1.0/users/info%40blind.dk/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Graph link is outside the approved mailbox.");
        }
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Add("Prefer", new[] { "outlook.body-content-type=\"text\"", "IdType=\"ImmutableId\"" });
        return request;
    }
}

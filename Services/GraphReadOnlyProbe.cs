using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed class GraphReadOnlyProbe
{
    private const int MaximumMessages = 5;
    private const string InboxUrl = "https://graph.microsoft.com/v1.0/users/info%40blind.dk/mailFolders/inbox/messages"
        + "?$top=5&$select=id,subject,body,receivedDateTime&$orderby=receivedDateTime%20desc";
    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly IMailClassifier _classifier;
    private readonly AiShadowObserver? _shadow;

    public GraphReadOnlyProbe(HttpClient http, TokenCredential credential, IMailClassifier classifier,
        AiShadowObserver? shadow = null)
    {
        _http = http;
        _credential = credential;
        _classifier = classifier;
        _shadow = shadow;
    }

    public async Task RunAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var messages = await FetchAsync(InboxUrl, MaximumMessages, cancellationToken);

        await output.WriteLineAsync($"Read-only test: {messages.Count} messages from info@blind.dk. No mailbox changes.");
        foreach (var message in messages)
        {
            var decision = await _classifier.ClassifyAsync(message, cancellationToken);
            var suggestion = decision.Route == MailRoute.Forward ? "forward suggestion" : "manual review";
            await output.WriteLineAsync($"Id={JsonSerializer.Serialize(message.Id)} Received={message.ReceivedAt:O} Suggestion={suggestion}");
        }

        await output.WriteLineAsync("Read-only test completed. No messages were sent, moved, deleted or marked as read.");
    }

    public async Task RunExamplesAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var categories = new (string Name, string[] Keywords)[]
        {
            ("Order", new[] { "ordre", "bestilling", "levering" }),
            ("Invoice", new[] { "faktura", "betaling", "invoice" }),
            ("General question", new[] { "sp\u00f8rgsm\u00e5l", "samarbejde" }),
            ("Do not forward", new[] { "ikke videresend", "holdes tilbage", "manuel behandling" })
        };
        foreach (var folder in new[] { "inbox", "deleteditems" })
        {
            var url = $"https://graph.microsoft.com/v1.0/users/info%40blind.dk/mailFolders/{folder}/messages"
                + "?$top=50&$select=id,subject,body,receivedDateTime&$orderby=receivedDateTime%20desc";
            var messages = await FetchAsync(url, 50, cancellationToken);
            var counts = new int[categories.Length, 2];
            var totalForward = 0;
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = await _classifier.ClassifyAsync(message, cancellationToken);
                var forward = decision.Route == MailRoute.Forward;
                if (forward)
                {
                    totalForward++;
                }

                var text = $"{message.Subject} {message.Body}";
                for (var index = 0; index < categories.Length; index++)
                {
                    if (categories[index].Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                    {
                        counts[index, forward ? 0 : 1]++;
                    }
                }
            }

            await output.WriteLineAsync($"Folder={folder} Read={messages.Count} ForwardSuggestions={totalForward} ManualReview={messages.Count - totalForward}");
            for (var index = 0; index < categories.Length; index++)
            {
                await output.WriteLineAsync($"Category={categories[index].Name} ForwardSuggestions={counts[index, 0]} ManualReview={counts[index, 1]}");
            }
        }

        await output.WriteLineAsync("Read-only example scan completed. Categories overlap and are keyword candidates, not verified labels.");
        await output.WriteLineAsync("At most one page of 50 messages per folder was read; older messages and subfolders were not searched. No mailbox changes.");
    }

    public async Task RunSummaryAsync(TextWriter output, DateTimeOffset startTime, CancellationToken cancellationToken,
        bool includeRoutingDetails = false)
    {
        var filter = Uri.EscapeDataString($"receivedDateTime ge {startTime.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffffZ}");
        var messages = await FetchAsync($"{InboxUrl}&$filter={filter}", MaximumMessages, cancellationToken);
        if (messages.Any(message => message.ReceivedAt < startTime))
        {
            throw new InvalidDataException("Graph returned a message before the approved start time.");
        }

        var forward = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await _classifier.ClassifyAsync(message, cancellationToken);
            if (_shadow is not null)
            {
                await _shadow.ObserveAsync(message, decision, cancellationToken);
            }
            if (decision.Route == MailRoute.Forward)
            {
                forward++;
            }

            if (includeRoutingDetails)
            {
                await output.WriteLineAsync("RoutingPreview " + JsonSerializer.Serialize(new
                {
                    Reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Id))),
                    ReceivedUtc = message.ReceivedAt.ToUniversalTime(),
                    Status = decision.Route == MailRoute.Forward ? "RecipientSuggestion" : "ManualReview",
                    Rules = decision.MatchedRules,
                    CandidateTo = decision.Recipients,
                    CandidateCc = decision.CcRecipients,
                    Unresolved = decision.UnresolvedRecipients,
                    ReviewReason = decision.Reason,
                    SendingEnabled = false
                }));
            }
        }

        await output.WriteLineAsync($"Read={messages.Count} ForwardSuggestions={forward} ManualReview={messages.Count - forward}. Sample only; repeated samples may overlap. No mailbox changes.");
    }

    private async Task<IReadOnlyList<EmailMessage>> FetchAsync(string url, int maximumMessages, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        // Never follow pagination: this is a single, bounded read, not an inbox worker.
        return await ReadMessagesAsync(response.Content, maximumMessages, cancellationToken);
    }

    private static async Task<IReadOnlyList<EmailMessage>> ReadMessagesAsync(HttpContent content, int maximumMessages, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var values = document.RootElement.GetProperty("value");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > maximumMessages)
            {
                throw new InvalidDataException($"Graph must return an array of no more than {maximumMessages} messages.");
            }

            var messages = new List<EmailMessage>();
            foreach (var value in values.EnumerateArray())
            {
                var id = value.GetProperty("id").GetString();
                var body = value.GetProperty("body");
                if (string.IsNullOrWhiteSpace(id) ||
                    !string.Equals(body.GetProperty("contentType").GetString(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Graph must return message IDs and plain text bodies.");
                }

                messages.Add(new EmailMessage
                {
                    Id = id,
                    Subject = value.GetProperty("subject").GetString() ?? "",
                    Body = body.GetProperty("content").GetString() ?? "",
                    ReceivedAt = value.GetProperty("receivedDateTime").GetDateTimeOffset()
                });
            }

            return messages;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Graph returned an invalid message response.");
        }
    }
}

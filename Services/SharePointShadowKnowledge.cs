using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Azure.Core;
using InfoMail.Models;

namespace InfoMail.Services;

public interface IShadowKnowledgeSource
{
    Task<ShadowRules> EnrichAsync(ShadowRules rules, CancellationToken cancellationToken);
    Task<ShadowRules> EnrichAsync(ShadowRules rules, EmailMessage message, CancellationToken cancellationToken) =>
        EnrichAsync(rules, cancellationToken);
}

public sealed class SharePointShadowKnowledge(HttpClient http, TokenCredential credential,
    SharePointShadowComparison? comparison = null) : IShadowKnowledgeSource
{
    private const string Site = "https://graph.microsoft.com/v1.0/sites/" + SharePointRuleSource.SiteId;
    private const string ListId = "4bb4cf29-5e84-4332-bae1-d4c18861cd76";
    private static readonly (string Id, string Name)[] Documents =
    {
        ("01XKE5VJUIORBQVWMUSNCL45TJX6VI5KHA", "2.1 Info-mailen - stikordsliste_version maj2026.docx"),
        ("01XKE5VJXN6XXYVO5ZWFFZFL72CLVU6VPO", "Liste med n\u00f8gleord til sortering i infomailen.docx"),
        ("01XKE5VJQOSGH53PIOX5FK2WAYEB4F23P5", "regeltabel.docx")
    };
    private JsonObject? _snapshot;
    private Dictionary<string, string[]> _previousAiCandidates = new(StringComparer.Ordinal);
    private DateTimeOffset _nextRefresh;

    public Task<ShadowRules> EnrichAsync(ShadowRules rules, CancellationToken cancellationToken) =>
        EnrichAsync(rules, null, cancellationToken);

    public async Task<ShadowRules> EnrichAsync(ShadowRules rules, EmailMessage? message,
        CancellationToken cancellationToken)
    {
        if (_snapshot is null || DateTimeOffset.UtcNow >= _nextRefresh)
        {
            // Publish only complete snapshots; failed refreshes never silently use stale/partial sources.
            var documents = new JsonArray();
            var references = new JsonArray();
            foreach (var (id, name) in Documents)
            {
                using var metadata = await ReadJsonAsync(Site + "/drive/items/" + id, cancellationToken);
                var actualName = metadata.RootElement.GetProperty("name").GetString();
                if (actualName != name)
                    throw new InvalidDataException("Configured shadow document was renamed or replaced; review source selection.");
                var bytes = await DownloadAsync(Site + "/drive/items/" + id + "/content", cancellationToken);
                var text = ExtractWord(bytes);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                documents.Add(new JsonObject { ["Name"] = name, ["Content"] = text });
                references.Add(new JsonObject { ["Name"] = name, ["Version"] = hash });
            }

            var rows = new JsonArray();
            var rulesFromList = new JsonArray();
            var url = Site + "/lists/" + ListId + "/items?$expand=fields&$top=200";
            var complete = false;
            for (var page = 0; page < 10; page++)
            {
                using var items = await ReadJsonAsync(url, cancellationToken);
                foreach (var item in items.RootElement.GetProperty("value").EnumerateArray())
                {
                    var fields = item.GetProperty("fields");
                    var id = item.GetProperty("id").GetString() ?? throw new InvalidDataException("Missing list item ID.");
                    var active = fields.GetProperty("Aktiv").GetBoolean();
                    var external = fields.GetProperty("TilladEkstern").GetBoolean();
                    var minimum = fields.GetProperty("MinConfidence").GetDouble();
                    var title = fields.GetProperty("Title").GetString();
                    var recipient = fields.TryGetProperty("Modtager", out var address) ? address.GetString() : "";
                    if (string.IsNullOrWhiteSpace(title) || !double.IsFinite(minimum) || minimum is < 0 or > 100)
                        throw new InvalidDataException("MailRouting contains an invalid title or confidence threshold.");
                    var comment = fields.TryGetProperty("Kommentar", out var note) ? note.GetString() : "";
                    var row = new JsonObject
                    {
                        ["Id"] = "mailrouting-" + id, ["Title"] = title, ["Modtager"] = recipient,
                        ["Aktiv"] = active, ["MinConfidence"] = minimum, ["TilladEkstern"] = external,
                        ["Kommentar"] = comment
                    };
                    rows.Add(row);
                    if (!active)
                        continue;
                    var addresses = ParseRecipients(recipient, external);
                    rulesFromList.Add(new JsonObject
                    {
                        ["Id"] = "mailrouting-" + id, ["Topic"] = title,
                        ["To"] = new JsonArray(addresses.Select(value => JsonValue.Create(value)).ToArray()),
                        ["Cc"] = new JsonArray(), ["Keywords"] = new JsonArray(title),
                        ["Unresolved"] = addresses.Length == 0 ? new JsonArray("InvalidOrDisallowedListRecipient") : new JsonArray(),
                        ["ManualAction"] = minimum > 0
                            ? "MinConfidence is retained in its original units (fraction or percent), not a measured model probability. Manual verification of this threshold is required."
                            : addresses.Length == 0 ? "Manual recipient verification required." : null
                    });
                }
                if (rows.Count > 1000)
                    throw new InvalidDataException("MailRouting exceeds the 1000-row safety limit.");
                if (!items.RootElement.TryGetProperty("@odata.nextLink", out var next))
                {
                    complete = true;
                    break;
                }
                url = next.GetString() ?? throw new InvalidDataException("Missing list pagination URL.");
            }
            if (!complete || rows.Count == 0)
                throw new InvalidDataException("MailRouting must be complete and non-empty.");
            references.Add(new JsonObject
            {
                ["Name"] = "MailRouting",
                ["Version"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToJsonString())))
            });
            var feedbackRows = comparison is null
                ? new List<JsonElement>()
                : await comparison.ReadFeedbackAsync(cancellationToken);
            var employeeExamples = SharePointShadowComparison.BuildEmployeeFeedbackExamples(feedbackRows);
            var previousAiCandidates = BuildPreviousAiCandidateMap(feedbackRows);
            references.Add(new JsonObject
            {
                ["Name"] = "AI Mail Sammenligning",
                ["Version"] = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(employeeExamples.ToJsonString())))
            });
            var snapshot = new JsonObject
            {
                ["Documents"] = documents, ["MailRouting"] = rows, ["ListRules"] = rulesFromList,
                ["EmployeeFeedbackExamples"] = employeeExamples, ["SourceReferences"] = references
            };
            if (snapshot.ToJsonString().Length > 160_000)
                throw new InvalidDataException("Combined shadow sources exceed the safety limit; no source truncated.");
            _snapshot = snapshot;
            _previousAiCandidates = previousAiCandidates;
            _nextRefresh = DateTimeOffset.UtcNow.AddMinutes(5);
        }
        var merged = Merge(rules, _snapshot);
        if (message is null ||
            !_previousAiCandidates.TryGetValue(MessageKey(message.ReceivedAt, message.Subject), out var candidates))
            return merged;
        var catalog = JsonNode.Parse(merged.Catalog)?.AsObject()
            ?? throw new InvalidDataException("Merged shadow catalog is invalid.");
        catalog["CurrentMailAiCandidates"] = new JsonArray(candidates.Select(value => JsonValue.Create(value)).ToArray());
        var json = catalog.ToJsonString();
        return new ShadowRules(json, Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(merged.Version + "\n" + json))));
    }

    private static Dictionary<string, string[]> BuildPreviousAiCandidateMap(IReadOnlyList<JsonElement> rows)
    {
        var entries = rows.Select(row =>
        {
            if (!row.TryGetProperty("fields", out var fields) ||
                !fields.TryGetProperty("IsSynthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.False ||
                Text(fields, "EvaluationStatus") != "Completed")
                return null;
            var subject = Text(fields, "MailSubject");
            if (subject.Length == 0 || !DateTimeOffset.TryParse(Text(fields, "ReceivedUtc"), out var received))
                return null;
            var recipients = ParseRecipients(Text(fields, "AiTo"), false);
            return recipients.Length == 0 ? null : new
            {
                Key = MessageKey(received, subject),
                Recipients = recipients
            };
        }).Where(entry => entry is not null).Select(entry => entry!).ToArray();

        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var candidateSets = group.Select(entry => string.Join(";",
                entry.Recipients.Order(StringComparer.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidateSets.Length == 1)
                result[group.Key] = group.First().Recipients;
        }
        return result;
    }

    public static string[] FindPreviousAiCandidates(IReadOnlyList<JsonElement> rows, EmailMessage message) =>
        BuildPreviousAiCandidateMap(rows).TryGetValue(MessageKey(message.ReceivedAt, message.Subject), out var candidates)
            ? candidates : Array.Empty<string>();

    private static string MessageKey(DateTimeOffset received, string subject) =>
        JsonSerializer.Serialize(new[] { received.UtcDateTime.Ticks.ToString(
            System.Globalization.CultureInfo.InvariantCulture), subject });

    private static string Text(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    public static ShadowRules Merge(ShadowRules rules, JsonObject snapshot)
    {
        var catalog = JsonNode.Parse(rules.Catalog)?.AsObject() ?? throw new InvalidDataException("Missing shadow catalog.");
        var listRules = snapshot["ListRules"]?.AsArray() ?? throw new InvalidDataException("Missing MailRouting rules.");
        foreach (var rule in listRules)
            catalog["KeywordRules"]!.AsArray().Add(rule!.DeepClone());
        catalog["KnowledgeDocuments"] = snapshot["Documents"]!.DeepClone();
        catalog["MailRouting"] = snapshot["MailRouting"]!.DeepClone();
        catalog["EmployeeFeedbackExamples"] = snapshot["EmployeeFeedbackExamples"]?.DeepClone() ?? new JsonArray();
        catalog["CurrentMailAiCandidates"] = new JsonArray();
        catalog["SourceReferences"] = snapshot["SourceReferences"]!.DeepClone();
        var json = catalog.ToJsonString();
        return new ShadowRules(json, Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(rules.Version + "\n" + json))));
    }

    public static string[] ParseRecipients(string? text, bool allowExternal)
    {
        var addresses = (text ?? "").Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length == 0 || addresses.Any(value =>
            !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value ||
            value.Equals("info@blind.dk", StringComparison.OrdinalIgnoreCase) ||
            !allowExternal && !parsed.Host.Equals("blind.dk", StringComparison.OrdinalIgnoreCase)))
            return Array.Empty<string>();
        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string ExtractWord(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var part = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Word document part missing.");
        if (part.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Shadow document XML exceeds safety limit.");
        using var stream = part.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        if (document.Descendants(w + "ins").Any() || document.Descendants(w + "del").Any())
            throw new InvalidDataException("Shadow document has unresolved tracked changes.");
        var text = string.Join("\n", document.Descendants(w + "p").Select(paragraph =>
            string.Concat(paragraph.Descendants().Where(element =>
                element.Name == w + "t" || element.Name == w + "tab" || element.Name == w + "br")
                .Select(element => element.Name == w + "t" ? element.Value : " "))));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Shadow document has no readable text.");
        return text;
    }

    private async Task<JsonDocument> ReadJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await ReadBoundedAsync(response.Content, cancellationToken));
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Found)
        {
            response.EnsureSuccessStatusCode();
            return await ReadBoundedAsync(response.Content, cancellationToken);
        }
        var location = response.Headers.Location;
        if (location is null || !location.IsAbsoluteUri || location.Scheme != "https" ||
            location.Host != "blindesamfund.sharepoint.com" || location.UserInfo.Length != 0 || !location.IsDefaultPort)
            throw new InvalidDataException("Unexpected shadow document download redirect.");
        using var download = await http.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        download.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(download.Content, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        if (!url.StartsWith(Site + "/", StringComparison.Ordinal) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "graph.microsoft.com" ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Unexpected shadow source URL.");
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (memory.Length + count > 8 * 1024 * 1024)
                throw new InvalidDataException("Shadow source exceeds 8 MiB limit.");
            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        return memory.ToArray();
    }
}

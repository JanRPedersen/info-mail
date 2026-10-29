using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace InfoMail.Services;

public sealed class SharePointShadowComparison(HttpClient http, TokenCredential credential)
{
    public const string ListName = "AI Mail Sammenligning";
    private const string Site = "https://graph.microsoft.com/v1.0/sites/" + SharePointRuleSource.SiteId;
    private string? _listId;

    public async Task<string> EnsureAsync(CancellationToken cancellationToken)
    {
        if (_listId is not null)
            return _listId;
        var lists = await ReadPagesAsync(Site + "/lists?$select=id,displayName,webUrl", cancellationToken);
        var matches = lists.Where(list => list.GetProperty("displayName").GetString() == ListName).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException("Multiple AI comparison lists found; no list chosen.");
        string id;
        string webUrl;
        if (matches.Length == 0)
        {
            using var created = await SendAsync(HttpMethod.Post, Site + "/lists", Definition(), cancellationToken);
            created.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
            id = body.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException("Comparison list ID missing.");
            webUrl = body.RootElement.GetProperty("webUrl").GetString() ?? throw new InvalidDataException("Comparison URL missing.");
        }
        else
        {
            id = matches[0].GetProperty("id").GetString() ?? throw new InvalidDataException("Comparison list ID missing.");
            webUrl = matches[0].GetProperty("webUrl").GetString() ?? throw new InvalidDataException("Comparison URL missing.");
        }
        var columns = await ReadPagesAsync(Site + "/lists/" + id + "/columns", cancellationToken);
        foreach (var required in Columns())
        {
            var name = (string)required["name"];
            var column = columns.SingleOrDefault(value => value.GetProperty("name").GetString() == name);
            if (column.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("Comparison list missing required column: " + name);
            var type = required.ContainsKey("text") ? "text" : required.ContainsKey("choice") ? "choice" :
                required.ContainsKey("dateTime") ? "dateTime" : "boolean";
            if (!column.TryGetProperty(type, out _))
                throw new InvalidDataException("Comparison list column has incompatible type: " + name);
            if (name == "ShadowReference" &&
                (!column.TryGetProperty("enforceUniqueValues", out var unique) || !unique.GetBoolean()))
                throw new InvalidDataException("Comparison reference column must enforce uniqueness.");
        }
        _listId = id;
        return webUrl;
    }

    public static object Definition() => new
    {
        displayName = ListName,
        description = "AI-forslag og regelbeslutninger er ikke udfoerte handlinger. Medarbejderen udfylder Faktisk handling, Faktiske modtagere, Vurdering og Kommentar. Sletning beviser ikke videresendelse. Testdata er markeret.",
        list = new { template = "genericList" },
        columns = Columns()
    };

    public async Task<List<JsonElement>> ReadFeedbackAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return await ReadPagesAsync(Site + "/lists/" + _listId + "/items?$expand=fields&$top=200", cancellationToken);
    }

    public static JsonArray BuildEmployeeFeedbackExamples(IReadOnlyList<JsonElement> rows)
    {
        var actions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Videresendt", "Besvaret", "Manuel behandling", "Slettet uden videresendelse", "Andet"
        };
        var examples = rows.Select(row =>
        {
            if (!row.TryGetProperty("fields", out var fields) ||
                !fields.TryGetProperty("IsSynthetic", out var synthetic) || synthetic.ValueKind != JsonValueKind.False ||
                Text(fields, "ActualActor") != "Medarbejder")
                return null;
            var subject = Text(fields, "MailSubject");
            var action = Text(fields, "ActualAction");
            if (subject.Length == 0 || !actions.Contains(action))
                return null;
            var recipients = action == "Videresendt"
                ? SharePointShadowKnowledge.ParseRecipients(Text(fields, "ActualTo"), false)
                : Array.Empty<string>();
            if (action == "Videresendt" && recipients.Length == 0)
                return null;
            var received = Text(fields, "ReceivedUtc");
            return new
            {
                Received = DateTimeOffset.TryParse(received, out var date) ? date : DateTimeOffset.MinValue,
                Subject = subject,
                Action = action,
                Recipients = recipients
            };
        }).Where(example => example is not null)
            .Select(example => example!)
            .GroupBy(example => JsonSerializer.Serialize(new
            {
                example.Subject, example.Action, example.Recipients
            }), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(example => example.Received).First())
            .OrderByDescending(example => example.Received)
            .Take(200);
        var result = new JsonArray();
        foreach (var example in examples)
        {
            result.Add(new JsonObject
            {
                ["Subject"] = example.Subject,
                ["ActualAction"] = example.Action,
                ["ActualRecipients"] = new JsonArray(example.Recipients.Select(value => JsonValue.Create(value)).ToArray())
            });
        }
        return result;
    }

    private static string Text(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    public static bool NeedsEmployeeAttribution(JsonElement row)
    {
        var fields = row.GetProperty("fields");
        return fields.TryGetProperty("IsSynthetic", out var synthetic) && synthetic.ValueKind == JsonValueKind.False &&
            fields.TryGetProperty("ActualAction", out var action) && action.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(action.GetString()) && action.GetString() != "Ukendt" &&
            (!fields.TryGetProperty("ActualActor", out var actor) || actor.ValueKind == JsonValueKind.Null ||
                actor.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(actor.GetString()));
    }

    public async Task<int> ConfirmExistingEmployeeFeedbackAsync(CancellationToken cancellationToken)
    {
        var rows = (await ReadFeedbackAsync(cancellationToken)).Where(NeedsEmployeeAttribution).ToArray();
        if (rows.Length > 28)
            throw new InvalidDataException("More feedback rows than the confirmed snapshot; request a new confirmation.");
        var etags = rows.Select(row =>
            row.TryGetProperty("eTag", out var tag) && !string.IsNullOrWhiteSpace(tag.GetString())
                ? tag.GetString()! : throw new InvalidDataException("Feedback row ETag missing; no attribution without concurrency protection."))
            .ToArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var id = rows[index].GetProperty("id").GetString() ?? throw new InvalidDataException("Feedback row ID missing.");
            using var response = await SendAsync(HttpMethod.Patch, Site + "/lists/" + _listId + "/items/" + id + "/fields",
                new Dictionary<string, string> { ["ActualActor"] = "Medarbejder" }, cancellationToken, etags[index]);
            response.EnsureSuccessStatusCode();
        }
        return rows.Length;
    }

    public async Task UpsertAsync(ShadowLogEntry entry, CancellationToken cancellationToken)
    {
        SharePointShadowLog.FileName(entry);
        await EnsureAsync(cancellationToken);
        var url = Site + "/lists/" + _listId + "/items";
        var query = url + "?$expand=fields&$filter=" +
            Uri.EscapeDataString("fields/ShadowReference eq '" + entry.Reference + "'");
        var fields = MachineFields(entry);
        var existing = await ReadPagesAsync(query, cancellationToken);
        if (existing.Count > 1)
            throw new InvalidDataException("Duplicate comparison reference; manual reconciliation required.");
        if (existing.Count == 0)
        {
            using var created = await SendAsync(HttpMethod.Post, url, new { fields }, cancellationToken);
            if (created.IsSuccessStatusCode)
                return;
            // A previous upload may have committed even when its response was lost.
            if (created.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.BadRequest))
                created.EnsureSuccessStatusCode();
            existing = await ReadPagesAsync(query, cancellationToken);
            if (existing.Count != 1)
                created.EnsureSuccessStatusCode();
        }
        var id = existing.Single().GetProperty("id").GetString() ?? throw new InvalidDataException("Comparison row ID missing.");
        using var updated = await SendAsync(HttpMethod.Patch, url + "/" + id + "/fields", fields, cancellationToken);
        updated.EnsureSuccessStatusCode();
    }

    public static Dictionary<string, object> MachineFields(ShadowLogEntry entry) => new()
    {
        ["Title"] = entry.Subject.Length > 255 ? entry.Subject[..255] : entry.Subject,
        ["MailSubject"] = entry.Subject,
        ["ShadowReference"] = entry.Reference,
        ["ReceivedUtc"] = entry.ReceivedUtc.ToUniversalTime().ToString("O"),
        ["EvaluationStatus"] = entry.Status,
        ["IsSynthetic"] = entry.IsSynthetic,
        ["AiSuggestion"] = SharePointShadowLog.ActionText(entry.AiRoute),
        ["AiTo"] = string.Join("; ", entry.CandidateTo),
        ["AiCc"] = string.Join("; ", entry.CandidateCc),
        ["AiFolder"] = entry.TargetFolder ?? "",
        ["AiSummary"] = entry.Explanation ?? "Ingen AI-begrundelse tilgaengelig.",
        ["RuleSuggestion"] = SharePointShadowLog.ActionText(entry.BaselineRoute),
        ["RuleIds"] = string.Join("; ", entry.BaselineRuleIds),
        ["AiRuleIds"] = string.Join("; ", entry.AiRuleIds),
        ["SourceVersions"] = string.Join("\n", entry.Sources ?? Array.Empty<string>()),
        ["ModelVersion"] = entry.Deployment + " | " + entry.RuleVersion,
        ["SourceConflict"] = entry.SourceConflict == true
    };

    private static Dictionary<string, object>[] Columns()
    {
        static Dictionary<string, object> Text(string name, string label, bool multiline = false) => new()
        {
            ["name"] = name, ["displayName"] = label,
            ["text"] = new { allowMultipleLines = multiline, textType = "plain", appendChangesToExistingText = false }
        };
        static Dictionary<string, object> Choice(string name, string label, params string[] choices) => new()
        {
            ["name"] = name, ["displayName"] = label, ["choice"] = new { choices, allowTextEntry = false, displayAs = "dropDownMenu" }
        };
        var reference = Text("ShadowReference", "Reference");
        reference["indexed"] = true;
        reference["enforceUniqueValues"] = true;
        return new[]
        {
            reference, Text("MailSubject", "Mailens fulde emne", true),
            new Dictionary<string, object> { ["name"] = "ReceivedUtc", ["displayName"] = "Modtaget", ["dateTime"] = new { format = "dateTime", displayAs = "default" } },
            Text("EvaluationStatus", "Vurderingsstatus"),
            new Dictionary<string, object> { ["name"] = "IsSynthetic", ["displayName"] = "Testdata", ["boolean"] = new { } },
            Text("AiSuggestion", "AI ville"), Text("AiTo", "AI modtagerkandidater", true),
            Text("AiCc", "AI CC-kandidater", true), Text("AiFolder", "AI maalmappe"),
            Text("AiSummary", "Kort AI-begrundelse", true),
            Text("RuleSuggestion", "Regelrobot foreslog"), Text("RuleIds", "Regelrobot regel-ID", true),
            Text("AiRuleIds", "AI regel-ID", true), Text("SourceVersions", "Kilder og versioner", true),
            Text("ModelVersion", "Model og grundlagsversion", true),
            new Dictionary<string, object> { ["name"] = "SourceConflict", ["displayName"] = "Kildekonflikt", ["boolean"] = new { } },
            Choice("ActualAction", "Faktisk handling (udfyldes af medarbejder)",
                "Videresendt", "Besvaret", "Manuel behandling", "Slettet uden videresendelse", "Andet", "Ukendt"),
            Text("ActualTo", "Faktiske modtagere (udfyldes af medarbejder)", true),
            Choice("ActualActor", "Behandlet af (udfyldes af medarbejder)", "Medarbejder", "Regelrobot", "Ukendt"),
            Choice("HumanVerdict", "Vurdering (udfyldes af medarbejder)",
                "Enig med AI", "Delvist enig", "Uenig med AI", "Kan ikke vurderes"),
            Text("HumanComment", "Medarbejderens kommentar", true)
        };
    }

    private async Task<List<JsonElement>> ReadPagesAsync(string url, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        for (var page = 0; page < 20; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            items.AddRange(json.RootElement.GetProperty("value").EnumerateArray().Select(item => item.Clone()));
            if (!json.RootElement.TryGetProperty("@odata.nextLink", out var next))
                return items;
            url = next.GetString() ?? throw new InvalidDataException("Missing comparison pagination URL.");
        }
        throw new InvalidDataException("Comparison lookup exceeds its page limit.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body,
        CancellationToken cancellationToken, string? ifMatch = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !url.StartsWith(Site + "/", StringComparison.Ordinal) ||
            uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Unexpected comparison list URL.");
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, cancellationToken);
    }
}

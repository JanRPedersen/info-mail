using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed record ShadowLogEntry(string Reference, DateTimeOffset ReceivedUtc, string Subject,
    string Status, string Deployment, string RuleVersion, string BaselineRoute, string[] BaselineRuleIds,
    string? AiRoute, string[] AiRuleIds, string[] CandidateTo, string[] CandidateCc, string? TargetFolder,
    bool? RouteAgrees, bool? RulesAgree, string[]? Sources = null, bool? SourceConflict = null,
    string? Explanation = null, bool IsSynthetic = false);

public interface IShadowLogQueue
{
    Task QueueAsync(ShadowLogEntry entry, CancellationToken cancellationToken);
}

public sealed class SharePointShadowLog(
    TableClient table, HttpClient http, TokenCredential credential, ILogger<SharePointShadowLog> logger,
    SharePointShadowComparison? comparison = null) : IShadowLogQueue
{
    private const string Partition = "AiShadowLogV1";
    private const string Root = "https://graph.microsoft.com/v1.0/sites/" + SharePointRuleSource.SiteId + "/drive";

    public async Task QueueAsync(ShadowLogEntry entry, CancellationToken cancellationToken)
    {
        // A deterministic file can be uploaded again after a crash without duplicating it or repeating inference.
        var json = JsonSerializer.Serialize(entry);
        var saved = await table.GetEntityIfExistsAsync<TableEntity>(Partition, entry.Reference,
            cancellationToken: cancellationToken);
        if (saved.HasValue && entry.Status == "ClaimedOrInterrupted")
        {
            return;
        }
        if (saved.HasValue && saved.Value?.GetString("Json") == json)
        {
            return;
        }
        await table.UpsertEntityAsync(new TableEntity(Partition, entry.Reference)
        {
            ["Uploaded"] = false, ["Json"] = json
        }, TableUpdateMode.Merge, cancellationToken);
    }

    public async Task EnsureFolderAsync(CancellationToken cancellationToken)
    {
        using var existing = await SendAsync(HttpMethod.Get, Root + "/root:/logfiler", null, cancellationToken);
        if (existing.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await SendAsync(HttpMethod.Post, Root + "/root/children", JsonContent.Create(new Dictionary<string, object>
            {
                ["name"] = "logfiler", ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail"
            }), cancellationToken);
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                created.EnsureSuccessStatusCode();
            }
            using var verified = await SendAsync(HttpMethod.Get, Root + "/root:/logfiler", null, cancellationToken);
            verified.EnsureSuccessStatusCode();
            await VerifyFolderAsync(verified, cancellationToken);
        }
        else
        {
            existing.EnsureSuccessStatusCode();
            await VerifyFolderAsync(existing, cancellationToken);
        }
    }

    private static async Task VerifyFolderAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("folder", out var folder) || folder.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("SharePoint logfiler exists but is not a folder.");
        }
    }

    public async Task<bool> FlushAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await EnsureFolderAsync(timeout.Token);
            var count = 0;
            await foreach (var entity in table.QueryAsync<TableEntity>(
                $"PartitionKey eq '{Partition}' and Uploaded eq false", maxPerPage: 100, cancellationToken: timeout.Token))
            {
                var entry = JsonSerializer.Deserialize<ShadowLogEntry>(entity.GetString("Json")
                    ?? throw new InvalidDataException("Shadow outbox record missing."))
                    ?? throw new InvalidDataException("Shadow outbox record invalid.");
                var file = FileName(entry);
                using var response = await SendAsync(HttpMethod.Put, Root + "/root:/logfiler/" + file + ":/content",
                    new StringContent(Render(entry), Encoding.UTF8, "text/plain"), timeout.Token);
                response.EnsureSuccessStatusCode();
                if (comparison is not null)
                    await comparison.UpsertAsync(entry, timeout.Token);
                entity["Uploaded"] = true;
                await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, timeout.Token);
                logger.LogInformation("AI shadow SharePoint log uploaded. Reference={Reference}.", entry.Reference);
                if (++count >= 100)
                {
                    break;
                }
            }
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or RequestFailedException or
            Azure.Identity.AuthenticationFailedException or InvalidDataException or JsonException or OperationCanceledException)
        {
            logger.LogError("AI shadow SharePoint export failed ErrorType={Type} HTTP={Status}. Outbox retained for next scan.",
                error.GetType().Name, error is HttpRequestException httpError ? (int?)httpError.StatusCode : null);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public static string FileName(ShadowLogEntry entry)
    {
        if (entry.Reference.Length != 64 || entry.Reference.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Invalid shadow reference for file name.");
        }
        return $"{entry.ReceivedUtc.UtcDateTime:yyyyMMdd-HHmmss}-{entry.Reference}.txt";
    }

    public static string Render(ShadowLogEntry entry) =>
        "AI-SKYGGEDRIFT - FORSLAG, IKKE UDFOERTE MAILHANDLINGER\n" +
        (entry.IsSynthetic ? "SYNTETISK TEST - ikke en rigtig indbakke-mail.\n" : "RIGTIG INDBAKKE-MAIL - vurdering, ikke handling.\n") +
        $"Emne: {JsonSerializer.Serialize(entry.Subject)}\n" +
        $"Modtaget UTC: {entry.ReceivedUtc:O}\n" +
        $"Vurderingsstatus: {entry.Status}\n" +
        $"AI ville: {ActionText(entry.AiRoute)}\n" +
        $"Modtagerkandidater: {string.Join("; ", entry.CandidateTo)}\n" +
        $"CC-kandidater: {string.Join("; ", entry.CandidateCc)}\n" +
        $"Kort AI-begrundelse: {JsonSerializer.Serialize(entry.Explanation ?? "Ingen AI-begrundelse tilgaengelig.")}\n" +
        $"Regelrobot foreslog: {ActionText(entry.BaselineRoute)} (ikke dokumentation for udfoert handling)\n" +
        "Medarbejderens faktiske handling: IKKE REGISTRERET HER. Udfyld sammenligningslisten.\n" +
        "Sletning af originalen er ikke dokumentation for videresendelse.\n" +
        "AI-begrundelsen er modelgenereret og kan vaere forkert.\n\n" +
        "Forward betyder forslag om videresendelse. ManualReview betyder manuel vurdering.\n" +
        "Ingen mailtekst, vedhaeftninger eller afsender er gemt i denne log.\n" +
        "Modtagere vaelges kun fra aktive regelmodtagere eller registrerede medarbejderudfald; modellen kan ikke opfinde adresser.\n\n" +
        JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });

    public static string ActionText(string? route) => route switch
    {
        "Forward" => "Foreslaa videresendelse",
        "ManualReview" => "Foreslaa manuel vurdering",
        _ => "Ikke AI-vurderet"
    };

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content,
        CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), cancellationToken);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await http.SendAsync(request, cancellationToken);
    }

    public static ShadowLogEntry Create(EmailMessage message, string reference, string status, string deployment,
        ShadowRules rules, EmailDecision baseline, ShadowResult? result)
    {
        var to = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var sourceCatalog = JsonDocument.Parse(rules.Catalog);
        var sources = sourceCatalog.RootElement.TryGetProperty("SourceReferences", out var references)
            ? references.EnumerateArray().Select(source =>
                source.GetProperty("Name").GetString() + " | " + source.GetProperty("Version").GetString()).ToArray()
            : Array.Empty<string>();
        string? folder = null;
        if (result is not null)
        {
            foreach (var address in result.CandidateTo ?? Array.Empty<string>())
            {
                to.Add(address);
            }
            using var catalog = JsonDocument.Parse(rules.Catalog);
            foreach (var rule in catalog.RootElement.GetProperty("Topics").EnumerateArray()
                .Concat(catalog.RootElement.GetProperty("KeywordRules").EnumerateArray())
                .Where(rule => result.RuleIds.Contains(rule.GetProperty("Id").GetString(), StringComparer.Ordinal)))
            {
                if (rule.TryGetProperty("To", out var addresses))
                {
                    foreach (var address in addresses.EnumerateArray())
                        to.Add(address.GetString() ?? throw new InvalidDataException("Missing catalog recipient."));
                }
                if (rule.TryGetProperty("Cc", out var copies))
                {
                    foreach (var address in copies.EnumerateArray())
                        cc.Add(address.GetString() ?? throw new InvalidDataException("Missing catalog CC recipient."));
                }
            }
            if (result.Route == "Forward" || (result.CandidateTo?.Length ?? 0) > 0)
            {
                folder = catalog.RootElement.GetProperty("TargetFolder").GetString();
            }
        }
        return new ShadowLogEntry(reference, message.ReceivedAt.ToUniversalTime(), message.Subject,
            status, deployment, rules.Version, baseline.Route.ToString(), baseline.MatchedRules.ToArray(),
            result?.Route, result?.RuleIds ?? Array.Empty<string>(), to.Order().ToArray(),
            cc.Except(to).Order().ToArray(), folder, result?.RouteAgrees, result?.RulesAgree, sources, result?.SourceConflict,
            result?.Explanation, message.Id.StartsWith("synthetic-shadow-", StringComparison.Ordinal));
    }
}

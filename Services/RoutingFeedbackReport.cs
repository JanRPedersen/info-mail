using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Data.Tables;

namespace InfoMail.Services;

public sealed record FeedbackCandidate(string Topic, string[] ActualRecipients, int Examples,
    int ConflictingExamples, int ManualRuleExamples, string[] ComparisonRowIds, string Status);
public sealed record FeedbackAnalysis(int Rows, int RealObservations, int DistinctMessages,
    int EmployeeOutcomes, int MissingOrIncompleteOutcomes, int InvalidRecipientOutcomes,
    int ConflictingVersionOutcomes, Dictionary<string, int> EvaluationStatuses,
    FeedbackCandidate[] Candidates, int RowsWithActualAction, int RowsWithActualRecipients,
    int RowsAttributedToEmployee);

public sealed class RoutingFeedbackReport(
    SharePointShadowComparison comparison, TableClient table, HttpClient http, TokenCredential credential,
    ILogger<RoutingFeedbackReport> logger)
{
    private DateTimeOffset _nextReport;

    public async Task RunAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && DateTimeOffset.UtcNow < _nextReport)
            return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var analysis = Analyze(await comparison.ReadFeedbackAsync(timeout.Token));
            var stages = new Dictionary<string, int>(StringComparer.Ordinal);
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var count = 0;
            await foreach (var entity in table.QueryAsync<TableEntity>("PartitionKey eq 'Messages'",
                maxPerPage: 200, cancellationToken: timeout.Token))
            {
                if (++count > 20_000)
                    throw new InvalidDataException("Feedback report exceeds processing-state limit; no partial report published.");
                Increment(stages, entity.GetString("Stage") ?? throw new InvalidDataException("Processing stage missing."));
                Increment(reasons, entity.GetString("Reason") ?? throw new InvalidDataException("Processing reason missing."));
            }
            var text = Render(analysis, stages, reasons);
            var name = $"regelforslag-{DateTimeOffset.UtcNow:yyyyMMdd}.txt";
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }), timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Put,
                "https://graph.microsoft.com/v1.0/sites/" + SharePointRuleSource.SiteId +
                "/drive/root:/logfiler/" + name + ":/content");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Content = new StringContent(text, Encoding.UTF8, "text/plain");
            using var response = await http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            _nextReport = DateTimeOffset.UtcNow.AddDays(1);
            logger.LogInformation("Routing feedback report published File={File} RealObservations={Observations} DistinctMessages={Messages} EmployeeOutcomes={Outcomes} Incomplete={Incomplete} Candidates={Candidates}. No routing rules changed.",
                name, analysis.RealObservations, analysis.DistinctMessages, analysis.EmployeeOutcomes,
                analysis.MissingOrIncompleteOutcomes, analysis.Candidates.Length);
            if (force)
            {
                Console.WriteLine(text);
            }
        }
        catch (Exception error) when (error is HttpRequestException or Azure.RequestFailedException or
            Azure.Identity.AuthenticationFailedException or InvalidDataException or JsonException or
            InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            logger.LogError("Routing feedback report failed ErrorType={Type}. No partial report or rule change.",
                error.GetType().Name);
            if (force || cancellationToken.IsCancellationRequested)
                throw;
            _nextReport = DateTimeOffset.UtcNow.AddMinutes(30);
        }
    }

    public static FeedbackAnalysis Analyze(IReadOnlyList<JsonElement> rows)
    {
        var real = rows.Where(row => row.GetProperty("fields").TryGetProperty("IsSynthetic", out var test) &&
            test.ValueKind == JsonValueKind.False).ToArray();
        var statuses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in real)
            Increment(statuses, Text(row.GetProperty("fields"), "EvaluationStatus"));
        // References vary by source/model version. Conservatively collapse identical received time + full subject.
        var groups = real.GroupBy(row =>
        {
            var fields = row.GetProperty("fields");
            var received = Text(fields, "ReceivedUtc");
            var subject = Text(fields, "MailSubject");
            if (received.Length == 0 || subject.Length == 0)
                return "row:" + row.GetProperty("id").GetString();
            return JsonSerializer.Serialize(new[] { received, subject });
        }).ToArray();
        var evidence = new List<(string Topic, string Outcome, string[] To, string[] Rows, bool Manual)>();
        var missing = 0;
        var invalid = 0;
        var versionConflicts = 0;
        var actions = new HashSet<string>(StringComparer.Ordinal)
        {
            "Videresendt", "Besvaret", "Manuel behandling", "Slettet uden videresendelse", "Andet"
        };
        foreach (var group in groups)
        {
            var filled = group.Where(row =>
            {
                var fields = row.GetProperty("fields");
                return Text(fields, "ActualActor") == "Medarbejder" &&
                    actions.Contains(Text(fields, "ActualAction"));
            }).ToArray();
            if (filled.Length == 0)
            {
                missing++;
                continue;
            }
            var valid = new List<(JsonElement Row, string Action, string[] To)>();
            foreach (var row in filled)
            {
                var fields = row.GetProperty("fields");
                var action = Text(fields, "ActualAction");
                var recipients = action == "Videresendt"
                    ? SharePointShadowKnowledge.ParseRecipients(Text(fields, "ActualTo"), false)
                    : Array.Empty<string>();
                if (action == "Videresendt" && recipients.Length == 0)
                    continue;
                valid.Add((row, action, recipients));
            }
            if (valid.Count != filled.Length)
            {
                invalid++;
                continue;
            }
            if (valid.Select(item => item.Action + "|" + string.Join(";", item.To))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            {
                versionConflicts++;
                continue;
            }
            var topics = valid.Select(item => Topic(item.Row.GetProperty("fields"))).Distinct(StringComparer.Ordinal).ToArray();
            if (topics.Length != 1)
            {
                versionConflicts++;
                continue;
            }
            var fieldsForTopic = valid[0].Row.GetProperty("fields");
            // A stable existing taxonomy labels proposals; it is not treated as evidence of correctness.
            var topic = topics[0];
            evidence.Add((topic, valid[0].Action + "|" + string.Join(";", valid[0].To), valid[0].To,
                valid.Select(item => item.Row.GetProperty("id").GetString() ?? throw new InvalidDataException("Feedback row ID missing.")).ToArray(),
                Text(fieldsForTopic, "RuleSuggestion") == SharePointShadowLog.ActionText("ManualReview")));
        }
        var candidates = evidence.Where(item => item.Outcome.StartsWith("Videresendt|", StringComparison.Ordinal))
            .GroupBy(item => (item.Topic, item.Outcome))
            .Select(group =>
            {
                var conflicts = evidence.Count(item => item.Topic == group.Key.Topic && item.Outcome != group.Key.Outcome);
                return new FeedbackCandidate(group.Key.Topic, group.First().To, group.Count(), conflicts,
                    group.Count(item => item.Manual), group.SelectMany(item => item.Rows).ToArray(),
                    group.Key.Topic == "explicit-hold" ? "HoldOverrideRequiresSeparateReview" :
                    group.Key.Topic == "Unmatched" ? "NeedsExplicitTopicAndMatcher" :
                    conflicts > 0 ? "ConflictingHumanOutcomes" : "NeedsOwnerApprovalAndRuleTests");
            })
            .OrderByDescending(candidate => candidate.ManualRuleExamples)
            .ThenByDescending(candidate => candidate.Examples).ToArray();
        return new FeedbackAnalysis(rows.Count, real.Length, groups.Length, evidence.Count, missing,
            invalid, versionConflicts, statuses, candidates,
            real.Count(row => actions.Contains(Text(row.GetProperty("fields"), "ActualAction"))),
            real.Count(row => Text(row.GetProperty("fields"), "ActualTo").Length > 0),
            real.Count(row => Text(row.GetProperty("fields"), "ActualActor") == "Medarbejder"));
    }

    public static string Render(FeedbackAnalysis analysis, Dictionary<string, int> stages, Dictionary<string, int> reasons) =>
        "REGELFORSLAG FRA FAKTISK MEDARBEJDERBEHANDLING - IKKE AKTIVERET\n" +
        $"Oprettet UTC: {DateTimeOffset.UtcNow:O}\n" +
        $"Rigtige observationsraekker: {analysis.RealObservations}. Forskellige mails (konservativ optaelling): {analysis.DistinctMessages}.\n" +
        $"Brugbare medarbejderregistreringer: {analysis.EmployeeOutcomes}. Manglende/ufuldstaendige: {analysis.MissingOrIncompleteOutcomes}.\n" +
        $"Raekker med faktisk handling: {analysis.RowsWithActualAction}, faktiske modtagere: {analysis.RowsWithActualRecipients}, tilskrevet Medarbejder: {analysis.RowsAttributedToEmployee}.\n" +
        $"Ugyldige/eksterne modtagere: {analysis.InvalidRecipientOutcomes}. Modstridende versioner: {analysis.ConflictingVersionOutcomes}.\n" +
        $"Modtagergrupper til gennemgang: {analysis.Candidates.Length}.\n\n" +
        "Kun udfyldt faktisk handling fra Medarbejder er evidens. AI-forslag, enighedsfelter alene, robotbehandling og testdata er ikke evidens.\n" +
        "Ens fuldt emne + modtagelsestid grupperes for at undgaa at taelle kildeversioner som flere mails; identiske mails kan derfor blive undertalt.\n" +
        "En gruppe er IKKE en faerdig regel: emneafgraensning, praecise matchere, undtagelser, CC, modtagere og negativtests skal godkendes.\n" +
        "Eksterne videresendelser og manglende modtagere udelades. Ingen minimumsmaengde udgoer automatisk godkendelse.\n" +
        "Unmatched kraever manuel emneinddeling. Konflikter maa ikke skjules af et flertal.\n" +
        "Regeltilstande er historisk gemt siden produktionsstart, ikke antal mails i de seneste fem dage.\n" +
        "Accepted/Completed viser Graph-accept/tilstand, ikke verificeret levering. Indbakke-scantal maa ikke summeres: samme mail genlaeses.\n" +
        "Oversigten kan ikke bevise, hvor mange mails der blev fjernet af medarbejdere foer scanning.\n" +
        "Aabn eksemplerne via sammenligningslistens raekke-ID'er:\n" +
        "https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AI%20Mail%20Sammenligning\n\n" +
        JsonSerializer.Serialize(new { Analysis = analysis, ProcessingStages = stages, ProcessingReasons = reasons },
            new JsonSerializerOptions { WriteIndented = true });

    private static string Text(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";

    private static string Topic(JsonElement fields)
    {
        var topic = string.Join(";", Text(fields, "RuleIds").Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return topic.Length == 0 ? "Unmatched" : topic;
    }

    private static void Increment(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}

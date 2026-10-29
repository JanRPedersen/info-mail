using Azure;
using Azure.Data.Tables;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed record ShadowRules(string Catalog, string Version);
public enum ShadowClaim { Claimed, Existing, BudgetExceeded }
public sealed record ShadowResult(string Deployment, string RuleVersion, string BaselineRoute,
    string[] BaselineRuleIds, string Route, string[] RuleIds, bool RouteAgrees, bool RulesAgree,
    bool SourceConflict = false, string? Explanation = null, string[]? CandidateTo = null);

public sealed record ShadowObservation(bool Completed, EmailDecision? CandidateDecision);

public interface IAiShadowStore
{
    Task<ShadowRules> GetRulesAsync(CancellationToken cancellationToken);
    Task<bool> TryClaimAsync(string reference, int dailyLimit, CancellationToken cancellationToken);
    async Task<ShadowClaim> ClaimAsync(string reference, int dailyLimit, CancellationToken cancellationToken) =>
        await TryClaimAsync(reference, dailyLimit, cancellationToken) ? ShadowClaim.Claimed : ShadowClaim.Existing;
    Task CompleteAsync(string reference, ShadowResult result, CancellationToken cancellationToken);
    Task FailAsync(string reference, CancellationToken cancellationToken);
}

public sealed class TableAiShadowStore(TableClient table) : IAiShadowStore
{
    private const string Partition = "AiShadowV1";

    public async Task<ShadowRules> GetRulesAsync(CancellationToken cancellationToken)
    {
        var entity = (await table.GetEntityAsync<TableEntity>("Rules", "Current",
            cancellationToken: cancellationToken)).Value;
        return new ShadowRules(
            entity.GetString("Catalog") ?? throw new InvalidDataException("Shadow catalog missing."),
            entity.GetString("Hash") ?? throw new InvalidDataException("Shadow rule version missing."));
    }

    public async Task<bool> TryClaimAsync(string reference, int dailyLimit, CancellationToken cancellationToken) =>
        await ClaimAsync(reference, dailyLimit, cancellationToken) == ShadowClaim.Claimed;

    public async Task<ShadowClaim> ClaimAsync(string reference, int dailyLimit, CancellationToken cancellationToken)
    {
        var key = "Mail-" + reference;
        if ((await table.GetEntityIfExistsAsync<TableEntity>(Partition, key,
            cancellationToken: cancellationToken)).HasValue)
        {
            return ShadowClaim.Existing;
        }

        var day = "Budget-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var saved = await table.GetEntityIfExistsAsync<TableEntity>(Partition, day,
                cancellationToken: cancellationToken);
            var budget = saved.HasValue
                ? saved.Value ?? throw new InvalidDataException("Shadow budget entity missing.")
                : new TableEntity(Partition, day) { ["Count"] = 0 };
            var count = budget.GetInt32("Count") ?? throw new InvalidDataException("Shadow budget missing.");
            if (count < 0)
            {
                throw new InvalidDataException("Shadow budget is invalid.");
            }
            if (count >= dailyLimit)
            {
                return ShadowClaim.BudgetExceeded;
            }
            budget["Count"] = count + 1;
            var claim = new TableEntity(Partition, key)
            {
                ["Status"] = "Claimed",
                ["CreatedUtc"] = DateTimeOffset.UtcNow
            };
            try
            {
                // Claim and budget are atomic. Interrupted or failed calls are never automatically retried.
                await table.SubmitTransactionAsync(new[]
                {
                    new TableTransactionAction(TableTransactionActionType.Add, claim),
                    new TableTransactionAction(saved.HasValue
                        ? TableTransactionActionType.UpdateReplace : TableTransactionActionType.Add, budget, budget.ETag)
                }, cancellationToken);
                return ShadowClaim.Claimed;
            }
            catch (RequestFailedException error) when (error.Status is 409 or 412)
            {
                if ((await table.GetEntityIfExistsAsync<TableEntity>(Partition, key,
                    cancellationToken: cancellationToken)).HasValue)
                {
                    return ShadowClaim.Existing;
                }
            }
        }
        throw new InvalidOperationException("Shadow budget contention; no model request made.");
    }

    public Task CompleteAsync(string reference, ShadowResult result, CancellationToken cancellationToken) =>
        table.UpdateEntityAsync(new TableEntity(Partition, "Mail-" + reference)
        {
            ["Status"] = "Completed",
            ["Deployment"] = result.Deployment,
            ["RuleVersion"] = result.RuleVersion,
            ["BaselineRoute"] = result.BaselineRoute,
            ["BaselineRuleIds"] = System.Text.Json.JsonSerializer.Serialize(result.BaselineRuleIds),
            ["Route"] = result.Route,
            ["RuleIds"] = System.Text.Json.JsonSerializer.Serialize(result.RuleIds),
            ["RouteAgrees"] = result.RouteAgrees,
            ["RulesAgree"] = result.RulesAgree,
            ["SourceConflict"] = result.SourceConflict,
            ["Explanation"] = result.Explanation ?? "",
            ["CandidateTo"] = System.Text.Json.JsonSerializer.Serialize(result.CandidateTo ?? Array.Empty<string>())
        }, ETag.All, TableUpdateMode.Merge, cancellationToken);

    public Task FailAsync(string reference, CancellationToken cancellationToken) =>
        table.UpdateEntityAsync(new TableEntity(Partition, "Mail-" + reference) { ["Status"] = "Failed" },
            ETag.All, TableUpdateMode.Merge, cancellationToken);
}

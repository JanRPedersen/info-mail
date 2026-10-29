using Azure;
using Azure.Core;
using Azure.Data.Tables;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class TableAiShadowStoreTests
{
    [Fact]
    public async Task NewClaimAndBudgetAreAddedAtomically()
    {
        var table = new FakeTable();
        Assert.True(await new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        var transaction = Assert.Single(table.Transactions);
        Assert.Equal(2, transaction.Length);
        Assert.All(transaction, action => Assert.Equal(TableTransactionActionType.Add, action.ActionType));
        Assert.All(transaction, action => Assert.Equal("AiShadowV1", action.Entity.PartitionKey));
        Assert.Equal("Mail-reference", transaction[0].Entity.RowKey);
        Assert.Equal("Claimed", ((TableEntity)transaction[0].Entity).GetString("Status"));
        Assert.StartsWith("Budget-", transaction[1].Entity.RowKey);
        Assert.Equal(1, ((TableEntity)transaction[1].Entity).GetInt32("Count"));
    }

    [Fact]
    public async Task ExistingBudgetUsesCompareAndSwapAndAllowsExactlyTheLimit()
    {
        var table = new FakeTable { Count = 19 };
        Assert.True(await new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        var budget = Assert.Single(table.Transactions)[1];
        Assert.Equal(TableTransactionActionType.UpdateReplace, budget.ActionType);
        Assert.Equal(new ETag("\"version\""), budget.ETag);
        Assert.Equal(20, ((TableEntity)budget.Entity).GetInt32("Count"));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    public async Task ExhaustedBudgetMakesNoWrite(int count)
    {
        var table = new FakeTable { Count = count };
        Assert.False(await new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Empty(table.Transactions);
    }

    [Fact]
    public async Task ClaimResultDistinguishesDailyLimitFromExistingObservation()
    {
        var table = new FakeTable { Count = 100 };
        Assert.Equal(ShadowClaim.BudgetExceeded,
            await new TableAiShadowStore(table).ClaimAsync("reference", 100, CancellationToken.None));
        table.Claimed = true;
        Assert.Equal(ShadowClaim.Existing,
            await new TableAiShadowStore(table).ClaimAsync("reference", 100, CancellationToken.None));
    }
    [Fact]
    public async Task ExistingMessageClaimIsNotReusedAfterRestart()
    {
        var table = new FakeTable { Claimed = true };
        Assert.False(await new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Empty(table.Transactions);
        Assert.Equal(1, table.Reads);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(412)]
    public async Task ConcurrentBudgetChangesRetryOnlyTheAtomicTransaction(int status)
    {
        var table = new FakeTable { Count = 3, ConflictsRemaining = 1, ConflictStatus = status };
        Assert.True(await new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Equal(2, table.Transactions.Count);
    }

    [Fact]
    public async Task ConcurrentMessageClaimStopsWithoutDuplicateCall()
    {
        var table = new FakeTable { ConflictsRemaining = 1, ClaimOnConflict = true };
        Assert.False(await new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Single(table.Transactions);
    }

    [Fact]
    public async Task PersistentContentionFailsExplicitlyAfterThreeAttempts()
    {
        var table = new FakeTable { ConflictsRemaining = 10 };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Equal(3, table.Transactions.Count);
    }

    [Fact]
    public async Task StorageAuthorizationFailureIsNotTreatedAsAvailableBudget()
    {
        var table = new FakeTable { ConflictsRemaining = 1, ConflictStatus = 403 };
        await Assert.ThrowsAsync<RequestFailedException>(() =>
            new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Single(table.Transactions);
    }

    [Fact]
    public async Task CorruptBudgetFailsClosed()
    {
        var table = new FakeTable { Count = -1 };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TableAiShadowStore(table).TryClaimAsync("reference", 20, CancellationToken.None));
        Assert.Empty(table.Transactions);
    }

    [Fact]
    public async Task CompletionPersistsComparisonMetadataWithoutMailContent()
    {
        var table = new FakeTable();
        var result = new ShadowResult("gpt-5.4-mini", "rule-version", "Forward",
            new[] { "donors" }, "ManualReview", Array.Empty<string>(), false, false);
        await new TableAiShadowStore(table).CompleteAsync("reference", result, CancellationToken.None);
        var saved = Assert.IsType<TableEntity>(table.Updated);
        Assert.Equal("AiShadowV1", saved.PartitionKey);
        Assert.Equal("Mail-reference", saved.RowKey);
        Assert.Equal("Completed", saved.GetString("Status"));
        Assert.Equal("gpt-5.4-mini", saved.GetString("Deployment"));
        Assert.Equal("rule-version", saved.GetString("RuleVersion"));
        Assert.Equal("Forward", saved.GetString("BaselineRoute"));
        Assert.Equal("[\"donors\"]", saved.GetString("BaselineRuleIds"));
        Assert.Equal("ManualReview", saved.GetString("Route"));
        Assert.False(saved.GetBoolean("RouteAgrees"));
        Assert.False(saved.ContainsKey("Subject"));
        Assert.False(saved.ContainsKey("Body"));
        Assert.False(saved.ContainsKey("MessageId"));
        Assert.Equal(TableUpdateMode.Merge, table.UpdateMode);
    }

    [Fact]
    public async Task FailureMarksExistingClaimRatherThanDeletingIt()
    {
        var table = new FakeTable();
        await new TableAiShadowStore(table).FailAsync("reference", CancellationToken.None);
        var saved = Assert.IsType<TableEntity>(table.Updated);
        Assert.Equal("Mail-reference", saved.RowKey);
        Assert.Equal("Failed", saved.GetString("Status"));
        Assert.Equal(TableUpdateMode.Merge, table.UpdateMode);
    }

    private sealed class FakeTable : TableClient
    {
        public int? Count { get; set; }
        public bool Claimed { get; set; }
        public int Reads { get; private set; }
        public int ConflictsRemaining { get; set; }
        public int ConflictStatus { get; set; } = 409;
        public bool ClaimOnConflict { get; set; }
        public List<TableTransactionAction[]> Transactions { get; } = new();
        public ITableEntity? Updated { get; private set; }
        public TableUpdateMode? UpdateMode { get; private set; }

        public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch,
            TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
        {
            Updated = entity;
            UpdateMode = mode;
            return Task.FromResult<Response>(new RawResponse());
        }

        public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            Reads++;
            TableEntity? entity = null;
            if (rowKey.StartsWith("Mail-", StringComparison.Ordinal) && Claimed)
            {
                entity = new TableEntity(partitionKey, rowKey);
            }
            if (rowKey.StartsWith("Budget-", StringComparison.Ordinal) && Count.HasValue)
            {
                entity = new TableEntity(partitionKey, rowKey) { ["Count"] = Count.Value, ETag = new ETag("\"version\"") };
            }
            if (entity is not null && entity is not T)
            {
                throw new InvalidOperationException("Unexpected table entity type.");
            }
            return Task.FromResult<NullableResponse<T>>(new Optional<T>(entity as T));
        }

        public override Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default)
        {
            Transactions.Add(transactionActions.ToArray());
            if (ConflictsRemaining-- > 0)
            {
                Claimed = ClaimOnConflict;
                throw new RequestFailedException(ConflictStatus, "Simulated storage error.");
            }
            return Task.FromResult(Response.FromValue<IReadOnlyList<Response>>(Array.Empty<Response>(), new RawResponse()));
        }
    }

    private sealed class Optional<T>(T? value) : NullableResponse<T> where T : class
    {
        public override bool HasValue => value is not null;
        public override T Value => value ?? throw new InvalidOperationException("No entity.");
        public override Response GetRawResponse() => new RawResponse();
    }

    private sealed class RawResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => Array.Empty<HttpHeader>();
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        { values = Array.Empty<string>(); return false; }
    }
}

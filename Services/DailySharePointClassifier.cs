using Azure;
using Azure.Data.Tables;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed class DailySharePointClassifier : IMailClassifier
{
    private readonly SharePointRuleSource _source;
    private readonly TableClient _table;
    private readonly ILogger<DailySharePointClassifier> _logger;
    private readonly string _baseline;
    private CatalogMailClassifier? _active;
    private string? _hash;
    private DateTimeOffset _nextCheck;
    private bool _loaded;

    public DailySharePointClassifier(SharePointRuleSource source, TableClient table,
        ILogger<DailySharePointClassifier> logger, string baseline)
    {
        _source = source;
        _table = table;
        _logger = logger;
        _baseline = baseline;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            var saved = await _table.GetEntityIfExistsAsync<TableEntity>("Rules", "Current", cancellationToken: cancellationToken);
            if (saved.HasValue)
            {
                var entity = saved.Value ?? throw new InvalidDataException("Saved rule state missing.");
                var catalog = entity.GetString("Catalog") ?? throw new InvalidDataException("Saved catalog missing.");
                _active = CatalogMailClassifier.FromJson(catalog);
                _hash = entity.GetString("Hash");
                _nextCheck = entity.GetDateTimeOffset("NextCheck") ?? throw new InvalidDataException("Saved rule check time missing.");
                _logger.LogInformation("SharePointRules restored. Version={Version} NextCheckUtc={NextCheck}. Sending=false.",
                    _hash, _nextCheck);
            }
            _loaded = true;
        }
        if (DateTimeOffset.UtcNow < _nextCheck)
        {
            return;
        }
        var snapshot = await _source.ReadAsync(cancellationToken);
        var json = WordRuleTable.Apply(_baseline, snapshot);
        if (System.Text.Encoding.Unicode.GetByteCount(json) > 60_000)
        {
            throw new InvalidDataException("Validated catalog exceeds the state property size limit.");
        }
        var classifier = CatalogMailClassifier.FromJson(json);
        var nextCheck = DateTimeOffset.UtcNow.AddDays(1);
        var current = new TableEntity("Rules", "Current")
        {
            ["Catalog"] = json, ["Hash"] = snapshot.Hash, ["NextCheck"] = nextCheck
        };
        var version = new TableEntity("Rules", snapshot.Hash)
        {
            ["Catalog"] = json, ["Hash"] = snapshot.Hash, ["NextCheck"] = nextCheck
        };
        await _table.SubmitTransactionAsync(new[]
        {
            new TableTransactionAction(TableTransactionActionType.UpsertReplace, version),
            new TableTransactionAction(TableTransactionActionType.UpsertReplace, current)
        }, cancellationToken);
        _active = classifier;
        _nextCheck = nextCheck;
        _logger.LogInformation("SharePointRules validated. Rows={Rows} Changed={Changed} Version={Version} NextCheckUtc={NextCheck}. Sending=false.",
            snapshot.Rows.Count, _hash != snapshot.Hash, snapshot.Hash, nextCheck);
        _hash = snapshot.Hash;
    }

    public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _active?.ClassifyAsync(message, cancellationToken) ?? Task.FromResult(new EmailDecision
        {
            Route = MailRoute.ManualReview,
            Reason = "SharePointRulesUnavailable",
            UnresolvedRecipients = new() { "NoValidatedSharePointTable" }
        });
    }
}

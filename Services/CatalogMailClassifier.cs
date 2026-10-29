using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed class CatalogMailClassifier : IMailClassifier
{
    private readonly Catalog _catalog;
    private readonly Dictionary<string, Regex[]> _topicMatchers;
    private readonly Dictionary<string, Regex[]> _keywordMatchers;

    public CatalogMailClassifier(string path)
        : this(File.ReadAllText(path), true)
    {
    }

    private CatalogMailClassifier(string json, bool fromJson)
    {
        _catalog = JsonSerializer.Deserialize<Catalog>(json)
            ?? throw new InvalidDataException("Routing catalog is empty.");
        Validate();
        _topicMatchers = _catalog.TopicMatchers.ToDictionary(pair => pair.Key, pair => Compile(pair.Value));
        _keywordMatchers = _catalog.KeywordRules.ToDictionary(rule => rule.Id, rule => Compile(rule.Keywords));
    }

    public static CatalogMailClassifier FromJson(string json) => new(json, true);

    public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = $"{message.Subject} {message.Body}";
        if (RoutingDecisionService.ContainsNoForwardInstruction(text))
        {
            return Task.FromResult(new EmailDecision
            {
                Route = MailRoute.ManualReview,
                Reason = "ExplicitHold",
                MatchedRules = new() { "explicit-hold" }
            });
        }

        var topics = _catalog.Topics.Where(rule => Matches(_topicMatchers[rule.Id], text)).ToList();
        var keywords = _catalog.KeywordRules.Where(rule => Matches(_keywordMatchers[rule.Id], text)).ToList();
        var matched = topics.Concat(keywords).ToList();
        var to = Distinct(matched.SelectMany(rule => rule.To));
        var cc = Distinct(matched.SelectMany(rule => rule.Cc)).Except(to, StringComparer.OrdinalIgnoreCase).ToList();
        var unresolved = Distinct(matched.SelectMany(rule => rule.Unresolved));
        var review = topics.Count > 1 || topics.Any(rule => !_catalog.PreviewReadyTopicIds.Contains(rule.Id)) ||
            unresolved.Count > 0 || matched.Any(rule => !string.IsNullOrWhiteSpace(rule.ManualAction)) ||
            to.Count == 0;

        return Task.FromResult(new EmailDecision
        {
            Route = review ? MailRoute.ManualReview : MailRoute.Forward,
            Reason = matched.Count == 0 ? "NoCatalogMatch" : review ? "CatalogReviewRequired" : "CatalogRecipientSuggestion",
            Recipient = !review ? to.FirstOrDefault() : null,
            Recipients = to,
            CcRecipients = cc,
            MatchedRules = matched.Select(rule => rule.Id).ToList(),
            UnresolvedRecipients = unresolved,
            TargetFolder = !review ? _catalog.TargetFolder : null
        });
    }

    private void Validate()
    {
        var all = _catalog.Topics.Concat(_catalog.KeywordRules).ToList();
        if (_catalog.Status != "Draft" || _catalog.AutomaticSendingEnabled || string.IsNullOrWhiteSpace(_catalog.TargetFolder) ||
            all.Count == 0 || all.Any(rule => string.IsNullOrWhiteSpace(rule.Id)) ||
            all.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != all.Count ||
            !_catalog.TopicMatchers.Keys.ToHashSet().SetEquals(_catalog.Topics.Select(rule => rule.Id)) ||
            _catalog.PreviewReadyTopicIds.Any(id => !_catalog.Topics.Any(rule => rule.Id == id)) ||
            _catalog.TopicMatchers.Values.Any(terms => terms.Length == 0 || terms.Any(string.IsNullOrWhiteSpace)) ||
            _catalog.KeywordRules.Any(rule => rule.Keywords.Length == 0 || rule.Keywords.Any(string.IsNullOrWhiteSpace)))
        {
            throw new InvalidDataException("Routing catalog draft, IDs or match definitions are invalid.");
        }

        foreach (var address in all.SelectMany(rule => rule.To.Concat(rule.Cc)))
        {
            if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address ||
                address.Equals("info@blind.dk", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Routing catalog contains an invalid or self-referencing recipient.");
            }
        }
    }

    private static List<string> Distinct(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static Regex[] Compile(IEnumerable<string> terms) => terms.Select(term =>
        new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))).ToArray();

    private static bool Matches(IEnumerable<Regex> matchers, string text) => matchers.Any(matcher => matcher.IsMatch(text));

    private sealed class Catalog
    {
        public string Status { get; set; } = "";
        public bool AutomaticSendingEnabled { get; set; }
        public string TargetFolder { get; set; } = "";
        public List<CatalogRule> Topics { get; set; } = new();
        public List<CatalogRule> KeywordRules { get; set; } = new();
        public Dictionary<string, string[]> TopicMatchers { get; set; } = new();
        public List<string> PreviewReadyTopicIds { get; set; } = new();
    }

    private sealed class CatalogRule
    {
        public string Id { get; set; } = "";
        public string[] To { get; set; } = Array.Empty<string>();
        public string[] Cc { get; set; } = Array.Empty<string>();
        public string[] Unresolved { get; set; } = Array.Empty<string>();
        public string[] Keywords { get; set; } = Array.Empty<string>();
        public string? ManualAction { get; set; }
    }
}

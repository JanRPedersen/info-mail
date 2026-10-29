using InfoMail.Models;

namespace InfoMail.Services;

public class RoutingDecisionService
{
    internal const string NoForwardReason = "The message explicitly says it should not be forwarded";
    private static readonly string[] NoForwardKeywords = { "ikke videresend", "holdes tilbage", "manuel", "afventer", "vurderes" };
    private readonly IConfiguration _configuration;
    private readonly IDocumentReferenceService _documentReferenceService;

    public RoutingDecisionService(IConfiguration configuration, IDocumentReferenceService documentReferenceService)
    {
        _configuration = configuration;
        _documentReferenceService = documentReferenceService;
    }

    public List<ForwardingRule> GetForwardingRules()
    {
        return _configuration.GetSection("MailRobot:ForwardingRules").Get<List<ForwardingRule>>() ?? new List<ForwardingRule>();
    }

    public async Task<(bool ShouldForward, List<string> Addresses, string Reason, string? TargetFolder)> EvaluateAsync(string subject, string body, CancellationToken cancellationToken)
    {
        var combinedText = $"{subject} {body}".ToLowerInvariant();
        var rules = GetForwardingRules();
        var referenceTexts = await _documentReferenceService.GetReferenceTextsAsync(cancellationToken);
        var referenceText = string.Join(" ", referenceTexts).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(referenceText))
        {
            combinedText += " " + referenceText;
        }

        if (ContainsNoForwardInstruction(combinedText))
        {
            return (false, new List<string>(), NoForwardReason, null);
        }

        foreach (var rule in rules.Where(r => r.Enabled))
        {
            var matchedKeywords = rule.Keywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Where(keyword => combinedText.Contains(keyword.ToLowerInvariant()))
                .ToList();

            if (matchedKeywords.Any())
            {
                var shouldForward = rule.ForwardWhenAnyKeywordMatches ? matchedKeywords.Any() : matchedKeywords.Count == rule.Keywords.Count;
                if (shouldForward)
                {
                    return (true, rule.ForwardAddresses, $"Matched rule '{rule.Name}' with keywords: {string.Join(", ", matchedKeywords)}", rule.TargetFolder);
                }
            }
        }

        return (false, new List<string>(), "No forwarding rule matched", null);
    }

    internal static bool ContainsNoForwardInstruction(string text) =>
        NoForwardKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}

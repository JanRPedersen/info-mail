using InfoMail.Models;

namespace InfoMail.Services;

public class RuleBasedMailClassifier : IMailClassifier
{
    private readonly IConfiguration _configuration;
    private readonly RoutingDecisionService _routingDecisionService;

    public RuleBasedMailClassifier(IConfiguration configuration, RoutingDecisionService routingDecisionService)
    {
        _configuration = configuration;
        _routingDecisionService = routingDecisionService;
    }

    public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var text = $"{message.Subject} {message.Body}".ToLowerInvariant();
        var rules = _configuration.GetSection("MailRobot:RoutingRules").Get<List<RoutingRule>>() ?? new List<RoutingRule>();

        foreach (var rule in rules)
        {
            if (rule.Keywords.Any(keyword => text.Contains(keyword.ToLowerInvariant())))
            {
                return Task.FromResult(new EmailDecision
                {
                    Route = rule.Route.Equals("Forward", StringComparison.OrdinalIgnoreCase) ? MailRoute.Forward : MailRoute.ManualReview,
                    Reason = $"Matched rule '{rule.Name}'",
                    Confidence = 0.92,
                    Recipient = rule.Recipient ?? _configuration["MailRobot:ForwardRecipient"]
                });
            }
        }

        var (shouldForward, addresses, reason) = _routingDecisionService.EvaluateAsync(message.Subject, message.Body, cancellationToken).GetAwaiter().GetResult();
        if (shouldForward)
        {
            return Task.FromResult(new EmailDecision
            {
                Route = MailRoute.Forward,
                Reason = reason,
                Confidence = 0.88,
                Recipient = addresses.FirstOrDefault() ?? _configuration["MailRobot:ForwardRecipient"]
            });
        }

        if (text.Contains("bilag") || text.Contains("invoice") || text.Contains("betaling") || text.Contains("ordre"))
        {
            return Task.FromResult(new EmailDecision
            {
                Route = MailRoute.Forward,
                Reason = "Likely operational or administrative request",
                Confidence = 0.84,
                Recipient = _configuration["MailRobot:ForwardRecipient"]
            });
        }

        return Task.FromResult(new EmailDecision
        {
            Route = MailRoute.ManualReview,
            Reason = reason,
            Confidence = 0.65
        });
    }
}

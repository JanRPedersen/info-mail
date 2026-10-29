using InfoMail.Models;

namespace InfoMail.Services;

internal static class MembershipMagazineRouting
{
    private static readonly HashSet<string> TopicIds = new(StringComparer.Ordinal)
    {
        "magazine-submissions",
        "letters-to-editor",
        "missing-magazine"
    };

    public static bool Applies(EmailDecision? decision) =>
        decision is not null &&
        decision.Reason != "ExplicitHold" &&
        decision.MatchedRules.Any(TopicIds.Contains);

    public static EmailDecision? TryCreateExclusiveDecision(EmailDecision? decision)
    {
        if (decision is null || decision.Reason == "ExplicitHold" ||
            !decision.MatchedRules.Any(TopicIds.Contains))
            return null;

        return new EmailDecision
        {
            Route = MailRoute.Forward,
            Reason = "MembershipMagazineExclusive",
            Recipient = "medlemsbladet@blind.dk",
            Recipients = new() { "medlemsbladet@blind.dk" },
            MatchedRules = decision.MatchedRules.Where(TopicIds.Contains).ToList(),
            TargetFolder = "Videresendt via regler"
        };
    }
}

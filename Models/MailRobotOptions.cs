namespace InfoMail.Models;

public class MailRobotOptions
{
    public int PollingIntervalSeconds { get; set; } = 30;
    public string InboxDataPath { get; set; } = "Data/inbox-samples.json";
    public string ForwardRecipient { get; set; } = "team@company.com";
    public List<RoutingRule> RoutingRules { get; set; } = new();
}

public class RoutingRule
{
    public string Name { get; set; } = string.Empty;
    public string Route { get; set; } = "ManualReview";
    public List<string> Keywords { get; set; } = new();
    public string? Recipient { get; set; }
}

namespace InfoMail.Models;

public class ForwardingLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MessageId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset ForwardedAt { get; set; } = DateTimeOffset.UtcNow;
}

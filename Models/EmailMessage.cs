namespace InfoMail.Models;

public class EmailMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsAutomatic { get; set; }
}

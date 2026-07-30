namespace InfoMail.Models;

public enum MailRoute
{
    Forward,
    ManualReview
}

public class EmailDecision
{
    public MailRoute Route { get; set; }
    public string Reason { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string? Recipient { get; set; }
    public string? TargetFolder { get; set; }
}

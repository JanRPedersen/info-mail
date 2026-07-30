namespace InfoMail.Models;

public class ForwardingRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
    public List<string> ForwardAddresses { get; set; } = new();
    public bool ForwardWhenAnyKeywordMatches { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

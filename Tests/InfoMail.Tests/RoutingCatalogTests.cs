using System.Net.Mail;
using System.Text.Json;
using Xunit;

namespace InfoMail.Tests;

public class RoutingCatalogTests
{
    [Fact]
    public void Catalog_IsDraftWithNoAutomaticSending()
    {
        using var document = Load();
        var root = document.RootElement;
        Assert.Equal("Draft", root.GetProperty("Status").GetString());
        Assert.False(root.GetProperty("AutomaticSendingEnabled").GetBoolean());
        Assert.Equal("Videresendt via regler", root.GetProperty("TargetFolder").GetString());
    }

    [Fact]
    public void Catalog_HasUniqueIdsValidAddressesAndSourceReferences()
    {
        using var document = Load();
        var entries = document.RootElement.GetProperty("Topics").EnumerateArray()
            .Concat(document.RootElement.GetProperty("KeywordRules").EnumerateArray()).ToArray();
        Assert.Equal(entries.Length, entries.Select(entry => entry.GetProperty("Id").GetString()).Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.NotEmpty(entry.GetProperty("SourceParagraphs").EnumerateArray());
            Assert.All(entry.GetProperty("To").EnumerateArray(), address =>
            {
                var text = address.GetString()!;
                Assert.Equal(text, new MailAddress(text).Address);
                Assert.False(text.Equals("info@blind.dk", StringComparison.OrdinalIgnoreCase));
            });
        });
    }

    [Fact]
    public void Catalog_CoversEveryNonHeadingOverviewParagraph()
    {
        using var document = Load();
        var root = document.RootElement;
        var covered = root.GetProperty("Topics").EnumerateArray()
            .Concat(root.GetProperty("GlobalInstructions").EnumerateArray())
            .SelectMany(entry => entry.GetProperty("SourceParagraphs").EnumerateArray())
            .Select(value => value.GetInt32()).ToHashSet();
        var headings = new[] { 1, 2, 4, 5, 146 };
        foreach (var paragraph in Enumerable.Range(1, 155).Except(headings))
        {
            Assert.Contains(paragraph, covered);
        }
    }

    [Fact]
    public void Catalog_SharedKeywordsHaveBothRecipients()
    {
        using var document = Load();
        var shared = Assert.Single(document.RootElement.GetProperty("KeywordRules").EnumerateArray(),
            rule => rule.GetProperty("Id").GetString() == "shared-keywords");
        Assert.Equal(new[] { "indsamling@blind.dk", "medlemsservice@blind.dk" },
            shared.GetProperty("To").EnumerateArray().Select(value => value.GetString()));
    }

    private static JsonDocument Load() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "routing-catalog.json")));
}

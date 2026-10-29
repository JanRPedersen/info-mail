using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using InfoMail.Models;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class WordRuleTableTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static string Baseline => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "routing-catalog.json"));

    [Fact]
    public void Read_OnlyReadsTableAndHandlesRunsLinksAndMultipleRecipients()
    {
        using var doc = Create(("Arv og testamenter", "ARVESAGER@blind.dk og other@example.test"));
        var snapshot = WordRuleTable.Read(doc);

        var row = Assert.Single(snapshot.Rows);
        Assert.Equal("Arv og testamenter", row.Subject);
        Assert.Equal(new[] { "arvesager@blind.dk", "other@example.test" }, row.Addresses);
        Assert.Equal(64, snapshot.Hash.Length);
    }

    [Theory]
    [InlineData("not an address")]
    [InlineData("info@blind.dk")]
    [InlineData("")]
    [InlineData("Person <person@example.test>")]
    [InlineData("a@example.test og mangler")]
    public void Read_RejectsInvalidRecipients(string value)
    {
        using var doc = Create(("Arv og testamenter", value));
        Assert.Throws<InvalidDataException>(() => WordRuleTable.Read(doc));
    }

    [Fact]
    public void Read_RejectsDuplicateSubjectsIgnoringCaseAndWhitespace()
    {
        using var doc = Create(("Arv og testamenter", "a@example.test"), (" ARV  og testamenter ", "b@example.test"));
        Assert.Throws<InvalidDataException>(() => WordRuleTable.Read(doc));
    }

    [Fact]
    public void Read_HashIgnoresRowOrderAndRecipientCase()
    {
        using var first = Create(("EBU", "A@example.test"), ("EPSO", "b@example.test"));
        using var second = Create(("EPSO", "b@example.test"), ("EBU", "a@example.test"));
        Assert.Equal(WordRuleTable.Read(first).Hash, WordRuleTable.Read(second).Hash);
    }

    [Fact]
    public void Read_HashChangesWhenRecipientChanges()
    {
        using var first = Create(("EBU", "a@example.test"));
        using var second = Create(("EBU", "b@example.test"));
        Assert.NotEqual(WordRuleTable.Read(first).Hash, WordRuleTable.Read(second).Hash);
    }

    [Fact]
    public async Task Apply_UsesWordAddressesAndKeepsKnownMatching()
    {
        using var doc = Create(("Arv og testamenter", "new@example.test"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var decision = await classifier.ClassifyAsync(new EmailMessage { Subject = "Testamente" }, CancellationToken.None);

        Assert.Equal(MailRoute.Forward, decision.Route);
        Assert.Equal(new[] { "new@example.test" }, decision.Recipients);
        Assert.Contains("inheritance", decision.MatchedRules);
    }

    [Fact]
    public async Task Apply_MissingTopicDoesNotKeepOldAddress()
    {
        using var doc = Create(("EBU", "new@example.test"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var decision = await classifier.ClassifyAsync(new EmailMessage { Subject = "Testamente" }, CancellationToken.None);

        Assert.Equal(MailRoute.ManualReview, decision.Route);
        Assert.Empty(decision.Recipients);
        Assert.Contains("SubjectMissingFromSharePoint", decision.UnresolvedRecipients);
    }

    [Fact]
    public async Task Apply_UnknownSubjectHasManualCandidateOnly()
    {
        using var doc = Create(("Nyt særligt emne", "new@example.test"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var decision = await classifier.ClassifyAsync(new EmailMessage { Subject = "Nyt særligt emne" }, CancellationToken.None);

        Assert.Equal(MailRoute.ManualReview, decision.Route);
        Assert.Equal(new[] { "new@example.test" }, decision.Recipients);
        Assert.Contains("NewSubjectNeedsMatchReview", decision.UnresolvedRecipients);
    }

    [Fact]
    public async Task Apply_RemovesOrphanKeywordAddresses()
    {
        using var doc = Create(("EBU", "new@example.test"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var decision = await classifier.ClassifyAsync(new EmailMessage { Subject = "Medlemskab" }, CancellationToken.None);
        Assert.Empty(decision.Recipients);
        Assert.Equal(MailRoute.ManualReview, decision.Route);
    }

    [Fact]
    public async Task Apply_ResolvesKnownPeopleButDoesNotRelaxManualExceptions()
    {
        using var doc = Create(("Førerhunde", "hunde@blind.dk og politisksekretariat@blind.dk"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var decision = await classifier.ClassifyAsync(new EmailMessage { Subject = "Førerhund" }, CancellationToken.None);
        Assert.Empty(decision.UnresolvedRecipients);
        Assert.Equal(2, decision.Recipients.Count);
        Assert.Equal(MailRoute.ManualReview, decision.Route);
    }

    [Fact]
    public async Task Apply_AliasMapsToExistingTopic()
    {
        using var doc = Create(("DOT-kort (rejsekort til medlemmer)", "new@example.test"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var decision = await classifier.ClassifyAsync(new EmailMessage { Subject = "DOT-kort" }, CancellationToken.None);
        Assert.Equal(new[] { "dot-card" }, decision.MatchedRules);
        Assert.Equal(new[] { "new@example.test" }, decision.Recipients);
    }

    [Fact]
    public async Task Apply_TwoKnownAliasesCombineRecipientsForManualReview()
    {
        using var doc = Create(("Media / presse henvendelser", "a@example.test"), ("Presse / media henvendelser", "a@example.test"));
        var classifier = CatalogMailClassifier.FromJson(WordRuleTable.Apply(Baseline, WordRuleTable.Read(doc)));
        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = "media" }, CancellationToken.None);
        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Equal(new[] { "a@example.test" }, result.Recipients);
    }

    internal static MemoryStream Create(params (string Subject, string Addresses)[] rows)
    {
        XElement Cell(string value) => new(W + "tc", new XElement(W + "p",
            new XElement(W + "hyperlink", new XElement(W + "r", new XElement(W + "t", value)))));
        var xml = new XDocument(new XElement(W + "document", new XElement(W + "body",
            new XElement(W + "p", new XElement(W + "r", new XElement(W + "t", "Free notes must not be rules"))),
            new XElement(W + "tbl",
                new XElement(W + "tr", Cell("Emne"), Cell("Mailadresse")),
                rows.Select(row => new XElement(W + "tr", Cell(row.Subject), Cell(row.Addresses)))))));
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
            writer.Write(xml.ToString());
        }
        memory.Position = 0;
        return memory;
    }
}

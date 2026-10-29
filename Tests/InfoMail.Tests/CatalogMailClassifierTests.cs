using System.Text.Json.Nodes;
using InfoMail.Models;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class CatalogMailClassifierTests
{
    private static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "TestData", "routing-catalog.json");

    [Theory]
    [InlineData("Bidrag", "indsamling@blind.dk", "donor-keywords")]
    [InlineData("MEDLEMSKORT", "medlemsservice@blind.dk", "member-keywords")]
    [InlineData("Testamente", "arvesager@blind.dk", "inheritance")]
    [InlineData("DOT-kort", "dotkort@blind.dk", "dot-card")]
    [InlineData("Indlæg til medlemsbladet", "medlemsbladet@blind.dk", "magazine-submissions")]
    public async Task ClassifyAsync_KnownRecipientsComeFromCatalog(string text, string address, string rule)
    {
        var result = await Classify(text);

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal(new[] { address }, result.Recipients);
        Assert.Contains(rule, result.MatchedRules);
        Assert.Empty(result.CcRecipients);
        Assert.Empty(result.UnresolvedRecipients);
        Assert.Equal("Videresendt via regler", result.TargetFolder);
    }

    [Theory]
    [InlineData("Ny adresse")]
    [InlineData("Medlemskab")]
    [InlineData("Opkrævninger")]
    [InlineData("Opsigelse af abonnement")]
    public async Task ClassifyAsync_SharedKeywordsRetainBothRecipients(string text)
    {
        var result = await Classify(text);

        Assert.Equal(new[] { "indsamling@blind.dk", "medlemsservice@blind.dk" }, result.Recipients);
        Assert.Contains("shared-keywords", result.MatchedRules);
    }

    [Fact]
    public async Task ClassifyAsync_UnambiguousSharedKeywordSuggestsBothAtOnce()
    {
        var result = await Classify("Medlemskab");

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal(2, result.Recipients.Count);
    }

    [Theory]
    [InlineData("Medlemsbladet")]
    [InlineData("støtteben")]
    [InlineData("medlemmernes")]
    [InlineData("Bestilling af ny ordre")]
    public async Task ClassifyAsync_DoesNotUseSubstringOrPrototypeFallback(string text)
    {
        var result = await Classify(text);

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Empty(result.Recipients);
        Assert.Empty(result.MatchedRules);
        Assert.Equal("NoCatalogMatch", result.Reason);
    }

    [Fact]
    public async Task ClassifyAsync_StopInstructionRemovesAllRecipientSuggestions()
    {
        var result = await Classify("Kontingent", "IKKE VIDERESEND");

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Equal("ExplicitHold", result.Reason);
        Assert.Empty(result.Recipients);
        Assert.Equal(new[] { "explicit-hold" }, result.MatchedRules);
    }

    [Theory]
    [InlineData("EBU")]
    [InlineData("DH invitation")]
    [InlineData("Selveje")]
    [InlineData("Faktura")]
    [InlineData("Udenlandsk faktura")]
    [InlineData("Faktura vedrørende DBS boliger")]
    [InlineData("Sygemelding")]
    [InlineData("Førerhunde")]
    [InlineData("Legatansøgning fra virksomhed")]
    public async Task ClassifyAsync_UnresolvedOrAmbiguousTopicsRequireReview(string text)
    {
        var result = await Classify(text);

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Equal("CatalogReviewRequired", result.Reason);
        Assert.NotEmpty(result.MatchedRules);
        Assert.Null(result.Recipient);
        Assert.Null(result.TargetFolder);
        Assert.DoesNotContain("info@blind.dk", result.Recipients);
    }

    [Fact]
    public async Task ClassifyAsync_UnresolvedCcIsNotGuessed()
    {
        var result = await Classify("Læserbrev");

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Equal(new[] { "medlemsbladet@blind.dk" }, result.Recipients);
        Assert.Empty(result.CcRecipients);
        Assert.Contains("Politisk sekretariat (cc)", result.UnresolvedRecipients);
    }

    [Fact]
    public async Task ClassifyAsync_OverlappingKeywordsDoNotDuplicateAddresses()
    {
        var result = await Classify("Bidrag medlemskab kontingent");

        Assert.Equal(new[] { "indsamling@blind.dk", "medlemsservice@blind.dk" }, result.Recipients);
        Assert.Equal(3, result.MatchedRules.Count);
    }

    [Fact]
    public async Task ClassifyAsync_ResolvedCcIsPreservedInPreview()
    {
        using var directory = new TestDirectory();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(CatalogPath))!;
        json["KeywordRules"]![0]!["Cc"] = new JsonArray("review@example.test");
        var path = directory.FilePath("catalog.json");
        await File.WriteAllTextAsync(path, json.ToJsonString());

        var result = await new CatalogMailClassifier(path).ClassifyAsync(
            new EmailMessage { Body = "Bidrag" }, CancellationToken.None);

        Assert.Equal(new[] { "review@example.test" }, result.CcRecipients);
    }

    [Theory]
    [InlineData("self-recipient")]
    [InlineData("missing-matcher")]
    [InlineData("empty-keyword")]
    [InlineData("sending-enabled")]
    public void Constructor_RejectsInvalidCatalog(string failure)
    {
        using var directory = new TestDirectory();
        var json = JsonNode.Parse(File.ReadAllText(CatalogPath))!;
        switch (failure)
        {
            case "self-recipient":
                json["KeywordRules"]![0]!["To"] = new JsonArray("info@blind.dk");
                break;
            case "missing-matcher":
                json["TopicMatchers"]!.AsObject().Remove("ebu");
                break;
            case "empty-keyword":
                json["KeywordRules"]![0]!["Keywords"] = new JsonArray("");
                break;
            case "sending-enabled":
                json["AutomaticSendingEnabled"] = true;
                break;
        }
        var path = directory.FilePath("catalog.json");
        File.WriteAllText(path, json.ToJsonString());

        Assert.Throws<InvalidDataException>(() => new CatalogMailClassifier(path));
    }

    [Fact]
    public async Task ClassifyAsync_HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CatalogMailClassifier(CatalogPath).ClassifyAsync(new EmailMessage(), cancellation.Token));
    }

    private static Task<EmailDecision> Classify(string subject, string body = "") =>
        new CatalogMailClassifier(CatalogPath).ClassifyAsync(
            new EmailMessage { Subject = subject, Body = body }, CancellationToken.None);
}

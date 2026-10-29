using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class SharePointShadowKnowledgeTests
{
    private const string Catalog = """{"Topics":[{"Id":"existing"}],"KeywordRules":[]}""";

    [Theory]
    [InlineData("team@blind.dk", false, 1)]
    [InlineData("team@example.test", false, 0)]
    [InlineData("team@example.test", true, 1)]
    [InlineData("info@blind.dk", true, 0)]
    [InlineData("Person <team@blind.dk>", false, 0)]
    [InlineData("not an address", true, 0)]
    [InlineData("team@blind.dk;TEAM@blind.dk", false, 1)]
    [InlineData("team@blind.dk;external@example.test", false, 0)]
    public void ListRecipientValidationIsConservative(string value, bool external, int count) =>
        Assert.Equal(count, SharePointShadowKnowledge.ParseRecipients(value, external).Length);

    [Fact]
    public void WordExtractionIncludesParagraphsAndTableCellsWithoutExecutingContent()
    {
        var bytes = Word("""
            <w:p><w:r><w:t>First source paragraph</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Table subject</w:t></w:r></w:p></w:tc>
            <w:tc><w:p><w:r><w:t>recipient@blind.dk</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """);
        Assert.Equal("First source paragraph\nTable subject\nrecipient@blind.dk", SharePointShadowKnowledge.ExtractWord(bytes));
    }

    [Fact]
    public void EmployeeFeedbackExamplesIncludeOnlyRealKnownEmployeeActionsAndInternalRecipients()
    {
        using var rows = JsonDocument.Parse("""
            [
              {"id":"1","fields":{"IsSynthetic":false,"ActualActor":"Medarbejder","MailSubject":"Donation",
                "ActualAction":"Videresendt","ActualTo":"team@blind.dk","ReceivedUtc":"2026-10-25T10:00:00Z",
                "HumanComment":"PRIVATE-COMMENT"}},
              {"id":"2","fields":{"IsSynthetic":false,"ActualActor":"Regelrobot","MailSubject":"Robot",
                "ActualAction":"Videresendt","ActualTo":"team@blind.dk"}},
              {"id":"3","fields":{"IsSynthetic":true,"ActualActor":"Medarbejder","MailSubject":"Synthetic",
                "ActualAction":"Videresendt","ActualTo":"team@blind.dk"}},
              {"id":"4","fields":{"IsSynthetic":false,"ActualActor":"Medarbejder","MailSubject":"External",
                "ActualAction":"Videresendt","ActualTo":"team@example.test"}},
              {"id":"5","fields":{"IsSynthetic":false,"ActualActor":"Medarbejder","MailSubject":"Answered",
                "ActualAction":"Besvaret","ActualTo":""}}
            ]
            """);
        var examples = SharePointShadowComparison.BuildEmployeeFeedbackExamples(
            rows.RootElement.EnumerateArray().ToArray());
        Assert.Equal(2, examples.Count);
        Assert.Contains("team@blind.dk", examples.ToJsonString());
        Assert.Contains("Besvaret", examples.ToJsonString());
        Assert.DoesNotContain("PRIVATE-COMMENT", examples.ToJsonString());
        Assert.DoesNotContain("External", examples.ToJsonString());
        Assert.DoesNotContain("Robot", examples.ToJsonString());
    }

    [Fact]
    public void PreviousAiCandidatesRequireExactMailIdentityAndConsistentInternalRecipients()
    {
        using var rows = JsonDocument.Parse("""
            [
              {"fields":{"IsSynthetic":false,"EvaluationStatus":"Completed","MailSubject":"Exact",
                "ReceivedUtc":"2026-10-25T10:00:00Z","AiTo":"team@blind.dk"}},
              {"fields":{"IsSynthetic":false,"EvaluationStatus":"Completed","MailSubject":"Exact",
                "ReceivedUtc":"2026-10-25T10:00:00+00:00","AiTo":"TEAM@blind.dk"}},
              {"fields":{"IsSynthetic":true,"EvaluationStatus":"Completed","MailSubject":"Exact",
                "ReceivedUtc":"2026-10-25T10:00:00Z","AiTo":"other@blind.dk"}},
              {"fields":{"IsSynthetic":false,"EvaluationStatus":"Completed","MailSubject":"Different",
                "ReceivedUtc":"2026-10-25T10:00:00Z","AiTo":"other@blind.dk"}}
            ]
            """);
        var mail = new InfoMail.Models.EmailMessage
        {
            Subject = "Exact",
            ReceivedAt = DateTimeOffset.Parse("2026-10-25T10:00:00Z")
        };
        Assert.Equal(new[] { "team@blind.dk" },
            SharePointShadowKnowledge.FindPreviousAiCandidates(rows.RootElement.EnumerateArray().ToArray(), mail));

        using var conflicting = JsonDocument.Parse("""
            [
              {"fields":{"IsSynthetic":false,"EvaluationStatus":"Completed","MailSubject":"Exact",
                "ReceivedUtc":"2026-10-25T10:00:00Z","AiTo":"team@blind.dk"}},
              {"fields":{"IsSynthetic":false,"EvaluationStatus":"Completed","MailSubject":"Exact",
                "ReceivedUtc":"2026-10-25T10:00:00Z","AiTo":"other@blind.dk"}}
            ]
            """);
        Assert.Empty(SharePointShadowKnowledge.FindPreviousAiCandidates(
            conflicting.RootElement.EnumerateArray().ToArray(), mail));
    }

    [Fact]
    public void TrackedChangesFailInsteadOfChoosingAnUnapprovedVersion()
    {
        var bytes = Word("""<w:p><w:ins><w:r><w:t>Unapproved</w:t></w:r></w:ins></w:p>""");
        Assert.Throws<InvalidDataException>(() => SharePointShadowKnowledge.ExtractWord(bytes));
    }

    [Fact]
    public void AllSourceContentAndVersionsAreIncorporatedWithoutChangingBaselineCatalog()
    {
        var snapshot = JsonNode.Parse("""
            {"Documents":[{"Name":"first.docx","Content":"first text"},{"Name":"second.docx","Content":"second text"},
            {"Name":"third.docx","Content":"third text"}],
            "MailRouting":[{"Title":"donors","Aktiv":true}],
            "ListRules":[{"Id":"mailrouting-1","To":["team@blind.dk"]}],
            "SourceReferences":[{"Name":"first.docx","Version":"v1"},{"Name":"MailRouting","Version":"v2"}]}
            """)!.AsObject();
        var original = new ShadowRules(Catalog, "baseline");
        var merged = SharePointShadowKnowledge.Merge(original, snapshot);
        using var json = JsonDocument.Parse(merged.Catalog);
        Assert.Equal(3, json.RootElement.GetProperty("KnowledgeDocuments").GetArrayLength());
        Assert.Single(json.RootElement.GetProperty("KeywordRules").EnumerateArray());
        Assert.Single(json.RootElement.GetProperty("MailRouting").EnumerateArray());
        Assert.Equal(Catalog, original.Catalog);
        Assert.NotEqual("baseline", merged.Version);
        Assert.Equal(merged.Version, SharePointShadowKnowledge.Merge(original, snapshot).Version);
        snapshot["Documents"]![0]!["Content"] = "new text";
        Assert.NotEqual(merged.Version, SharePointShadowKnowledge.Merge(original, snapshot).Version);
    }

    [Fact]
    public async Task SourceReaderLoadsExactlyThreeDocumentsAndAllListPagesThenCachesCompleteSnapshot()
    {
        var handler = new SourcesHandler();
        using var http = new HttpClient(handler);
        var source = new SharePointShadowKnowledge(http, new Credential());
        var rules = await source.EnrichAsync(new ShadowRules(Catalog, "baseline"), CancellationToken.None);
        using var json = JsonDocument.Parse(rules.Catalog);
        Assert.Equal(3, json.RootElement.GetProperty("KnowledgeDocuments").GetArrayLength());
        Assert.Equal(5, json.RootElement.GetProperty("SourceReferences").GetArrayLength());
        Assert.Equal(3, json.RootElement.GetProperty("MailRouting").GetArrayLength());
        var listRules = json.RootElement.GetProperty("KeywordRules").EnumerateArray().ToArray();
        Assert.Equal(2, listRules.Length);
        Assert.Equal("mailrouting-1", listRules[0].GetProperty("Id").GetString());
        Assert.Empty(listRules[1].GetProperty("To").EnumerateArray());
        Assert.Contains("manual", listRules[1].GetProperty("ManualAction").GetString()!, StringComparison.OrdinalIgnoreCase);
        var calls = handler.Calls;
        await source.EnrichAsync(new ShadowRules(Catalog, "new baseline"), CancellationToken.None);
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(3, handler.Downloads);
    }

    [Fact]
    public async Task AFailedListPageDoesNotPublishOrCachePartialSources()
    {
        var handler = new SourcesHandler { FailSecondPage = true };
        using var http = new HttpClient(handler);
        var source = new SharePointShadowKnowledge(http, new Credential());
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            source.EnrichAsync(new ShadowRules(Catalog, "baseline"), CancellationToken.None));
        handler.FailSecondPage = false;
        var result = await source.EnrichAsync(new ShadowRules(Catalog, "baseline"), CancellationToken.None);
        Assert.Equal(6, handler.Downloads);
        Assert.Contains("mailrouting-3", result.Catalog);
    }

    [Fact]
    public async Task UntrustedPaginationDoesNotReceiveBearerToken()
    {
        var handler = new SourcesHandler { BadNextLink = true };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new SharePointShadowKnowledge(http, new Credential())
            .EnrichAsync(new ShadowRules(Catalog, "baseline"), CancellationToken.None));
        Assert.False(handler.UnexpectedHost);
    }

    internal static byte[] Word(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), Encoding.UTF8);
            writer.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>""" +
                body + "</w:body></w:document>");
        }
        return output.ToArray();
    }

    private sealed class SourcesHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Downloads { get; private set; }
        public bool FailSecondPage { get; set; }
        public bool BadNextLink { get; init; }
        public bool UnexpectedHost { get; private set; }
        private static readonly Dictionary<string, string> Names = new()
        {
            ["01XKE5VJUIORBQVWMUSNCL45TJX6VI5KHA"] = "2.1 Info-mailen - stikordsliste_version maj2026.docx",
            ["01XKE5VJXN6XXYVO5ZWFFZFL72CLVU6VPO"] = "Liste med n\u00f8gleord til sortering i infomailen.docx",
            ["01XKE5VJQOSGH53PIOX5FK2WAYEB4F23P5"] = "regeltabel.docx"
        };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            UnexpectedHost |= request.RequestUri!.Host != "graph.microsoft.com";
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/content"))
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Word("<w:p><w:r><w:t>Complete source text</w:t></w:r></w:p>"))
                });
            }
            var name = Names.FirstOrDefault(pair => path.EndsWith(pair.Key));
            if (name.Key is not null)
                return Task.FromResult(Json(JsonSerializer.Serialize(new { name = name.Value })));
            var second = request.RequestUri.Query.Contains("skiptoken");
            if (second && FailSecondPage)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var rows = second
                ? new[] { new { id = "3", fields = new { Title = "external", Modtager = "blocked@example.test", Aktiv = true, TilladEkstern = false, MinConfidence = 80.0 } } }
                : new[]
                {
                    new { id = "1", fields = new { Title = "active", Modtager = "team@blind.dk", Aktiv = true, TilladEkstern = false, MinConfidence = 0.0 } },
                    new { id = "2", fields = new { Title = "inactive", Modtager = "unused@blind.dk", Aktiv = false, TilladEkstern = false, MinConfidence = 0.0 } }
                };
            var response = JsonSerializer.SerializeToNode(new { value = rows })!.AsObject();
            if (!second)
                response["@odata.nextLink"] = BadNextLink ? "https://evil.example.test/data" :
                    request.RequestUri.GetLeftPart(UriPartial.Path) + "?$skiptoken=page2";
            return Task.FromResult(Json(response.ToJsonString()));
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class SharePointShadowLogTests
{
    private static ShadowLogEntry Entry() => new(new string('A', 64), DateTimeOffset.Parse("2026-10-22T12:00:00Z"),
        "<script>PRIVATE-SUBJECT</script>\n=1+1", "Completed", "gpt-5.4-mini", "rules", "Forward",
        new[] { "donors" }, "ManualReview", Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<string>(), null, false, false);

    [Fact]
    public void PlainTextLogContainsSubjectButNoBodyAndFilenameCannotContainSubject()
    {
        var entry = Entry();
        var text = SharePointShadowLog.Render(entry);
        Assert.Contains("PRIVATE-SUBJECT", text);
        Assert.Contains("FORSLAG, IKKE UDFOERTE", text);
        Assert.Contains("ManualReview", text);
        Assert.DoesNotContain("<script>", text);
        Assert.DoesNotContain("\n=1+1", text);
        Assert.Equal("20261022-120000-" + new string('A', 64) + ".txt", SharePointShadowLog.FileName(entry));
        Assert.Throws<InvalidDataException>(() => SharePointShadowLog.FileName(entry with { Reference = "../bad" }));
    }

    [Fact]
    public void RecipientsComeFromSelectedCatalogRulesNotModelText()
    {
        var rules = new ShadowRules("""
            {"Topics":[{"Id":"donors","To":["donors@example.test"],"Cc":["copy@example.test","donors@example.test"]},
            {"Id":"other","To":["other@example.test"]}],"KeywordRules":[],"TargetFolder":"Reviewed folder"}
            """, "version");
        var result = new ShadowResult("gpt-5.4-mini", "version", "ManualReview", Array.Empty<string>(),
            "Forward", new[] { "donors" }, false, false);
        var entry = SharePointShadowLog.Create(new EmailMessage { Subject = "subject", Body = "PRIVATE-BODY" },
            new string('A', 64), "Completed", result.Deployment, rules, new EmailDecision(), result);
        Assert.Equal(new[] { "donors@example.test" }, entry.CandidateTo);
        Assert.Equal(new[] { "copy@example.test" }, entry.CandidateCc);
        Assert.Equal("Reviewed folder", entry.TargetFolder);
        Assert.DoesNotContain("PRIVATE-BODY", SharePointShadowLog.Render(entry));
    }

    [Fact]
    public void FeedbackCandidateIsLoggedAndUsesTheConfiguredDestinationEvenWhenAiRouteIsManual()
    {
        var rules = new ShadowRules("""
            {"Topics":[],"KeywordRules":[],"TargetFolder":"Videresendt via regler"}
            """, "version");
        var result = new ShadowResult("gpt-5.4-mini", "version", "ManualReview", Array.Empty<string>(),
            "ManualReview", Array.Empty<string>(), false, false, CandidateTo: new[] { "team@blind.dk" });

        var entry = SharePointShadowLog.Create(new EmailMessage { Subject = "Donation" }, new string('A', 64),
            "Completed", result.Deployment, rules, new EmailDecision(), result);

        Assert.Equal(new[] { "team@blind.dk" }, entry.CandidateTo);
        Assert.Equal("Videresendt via regler", entry.TargetFolder);
    }

    [Fact]
    public async Task MissingFolderIsCreatedOnlyAtApprovedSiteAndVerified()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            Assert.Equal("graph.microsoft.com", request.RequestUri.Host);
            Assert.Contains(SharePointRuleSource.SiteId, request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("logfiler", body.RootElement.GetProperty("name").GetString());
                Assert.Equal("fail", body.RootElement.GetProperty("@microsoft.graph.conflictBehavior").GetString());
                return Json("""{"folder":{}}""");
            }
            return requests.Count == 1 ? new(HttpStatusCode.NotFound) : Json("""{"folder":{}}""");
        }));
        await Log(new FakeTable(), http).EnsureFolderAsync(CancellationToken.None);
        Assert.Equal(3, requests.Count);
        Assert.StartsWith("POST ", requests[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Found)]
    public async Task MissingPermissionOrRedirectIsNotSuccess(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Log(new FakeTable(), http).EnsureFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExistingFileNamedLogfilerIsNotUsedAsFolder()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("""{"file":{}}"""))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Log(new FakeTable(), http).EnsureFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedUploadRetainsOutboxAndNextFlushUsesSameFile()
    {
        var table = new FakeTable { Entry = Entry() };
        var uploads = new List<string>();
        var fail = true;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"folder":{}}""");
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("text/plain", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync();
            Assert.Contains("PRIVATE-SUBJECT", body);
            uploads.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }));
        var log = Log(table, http);
        Assert.False(await log.FlushAsync(CancellationToken.None));
        Assert.False(table.Uploaded);
        fail = false;
        Assert.True(await log.FlushAsync(CancellationToken.None));
        Assert.True(table.Uploaded);
        Assert.Equal(2, uploads.Count);
        Assert.Equal(uploads[0], uploads[1]);
        Assert.Contains("/logfiler/20261022-120000-", uploads[0]);
    }

    [Fact]
    public async Task QueuePersistsOnlyApprovedLogFieldsAndDoesNotRewriteIdenticalEntries()
    {
        var table = new FakeTable();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No Graph expected")));
        var log = Log(table, http);
        await log.QueueAsync(Entry(), CancellationToken.None);
        Assert.Equal(1, table.Upserts);
        Assert.Equal("PRIVATE-SUBJECT", table.Entry!.Subject.Replace("<script>", "").Split("</script>")[0]);
        await log.QueueAsync(Entry(), CancellationToken.None);
        Assert.Equal(1, table.Upserts);
        await log.QueueAsync(Entry() with { Status = "FailedOrUncertain" }, CancellationToken.None);
        Assert.Equal(2, table.Upserts);
    }

    private static SharePointShadowLog Log(TableClient table, HttpClient http) =>
        new(table, http, new Credential(), NullLogger<SharePointShadowLog>.Instance);
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class FakeTable : TableClient
    {
        public ShadowLogEntry? Entry { get; set; }
        public bool Uploaded { get; private set; }
        public int Upserts { get; private set; }
        private TableEntity Entity() => new("AiShadowLogV1", Entry!.Reference)
        {
            ["Json"] = JsonSerializer.Serialize(Entry), ["Uploaded"] = Uploaded
        };
        public override Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<NullableResponse<T>>(new Optional<T>(Entry is null ? null : Entity() as T));

        public override Task<Response> UpsertEntityAsync<T>(T entity, TableUpdateMode mode = TableUpdateMode.Merge,
            CancellationToken cancellationToken = default)
        {
            Entry = JsonSerializer.Deserialize<ShadowLogEntry>(((TableEntity)(ITableEntity)entity).GetString("Json")!);
            Upserts++;
            return Task.FromResult<Response>(new RawResponse());
        }
        public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null,
            IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            var entities = Entry is not null && !Uploaded && Entity() is T typed ? new[] { typed } : Array.Empty<T>();
            return AsyncPageable<T>.FromPages(new[] { Page<T>.FromValues(entities, null, new RawResponse()) });
        }
        public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch,
            TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
        {
            Uploaded = true;
            return Task.FromResult<Response>(new RawResponse());
        }
    }

    private sealed class Optional<T>(T? value) : NullableResponse<T> where T : class
    {
        public override bool HasValue => value is not null;
        public override T Value => value ?? throw new InvalidOperationException("No entity.");
        public override Response GetRawResponse() => new RawResponse();
    }
    private sealed class RawResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => Array.Empty<HttpHeader>();
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        { values = Array.Empty<string>(); return false; }
    }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

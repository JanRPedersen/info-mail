using System.Net;
using System.Text.Json;
using Azure.Core;
using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InfoMail.Tests;

public class GraphReadOnlyProbeTests
{
    [Fact]
    public async Task RunAsync_UsesOneBoundedGetAndOnlyPrintsSafeFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(5, includeNextLink: true));
        using var http = new HttpClient(handler);
        var credential = new StubCredential();
        var classifier = new RecordingClassifier();
        using var output = new StringWriter();

        await new GraphReadOnlyProbe(http, credential, classifier).RunAsync(output, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("graph.microsoft.com", request.Uri.Host);
        Assert.Equal("/v1.0/users/info%40blind.dk/mailFolders/inbox/messages", request.Uri.AbsolutePath);
        Assert.Contains("$top=5", request.Uri.Query);
        Assert.Contains("$select=id,subject,body,receivedDateTime", request.Uri.Query);
        Assert.Equal("outlook.body-content-type=\"text\"", request.Prefer);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.Equal(new[] { "https://graph.microsoft.com/.default" }, credential.Scopes);
        Assert.Equal(5, classifier.Messages.Count);
        Assert.All(classifier.Messages, message =>
        {
            Assert.Equal("PRIVATE-SUBJECT", message.Subject);
            Assert.Equal("PRIVATE-BODY", message.Body);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero), message.ReceivedAt);
        });
        Assert.Contains("forward suggestion", output.ToString());
        Assert.Contains("mail-0", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
        Assert.DoesNotContain("test-token", output.ToString());
        Assert.DoesNotContain("PRIVATE-REASON", output.ToString());
    }

    [Fact]
    public async Task RunAsync_EmptyInboxIsReportedWithoutInventingMessages()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(0));
        using var http = new HttpClient(handler);
        var classifier = new RecordingClassifier();
        using var output = new StringWriter();

        await new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunAsync(output, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Empty(classifier.Messages);
        Assert.Contains("0 messages", output.ToString());
        Assert.Contains("completed", output.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task RunAsync_HttpFailureDoesNotClassifyOrReportSuccess(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status, "PRIVATE-ERROR-DETAILS");
        using var http = new HttpClient(handler);
        var classifier = new RecordingClassifier();
        using var output = new StringWriter();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunAsync(output, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Empty(classifier.Messages);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":[{"id":"mail-1"}]}""")]
    [InlineData("""{"value":[{"id":"","subject":"","body":{"contentType":"text","content":""},"receivedDateTime":"2026-10-20T06:00:00Z"}]}""")]
    [InlineData("""{"value":[{"id":"mail-1","subject":"","body":{"contentType":"html","content":"PRIVATE"},"receivedDateTime":"2026-10-20T06:00:00Z"}]}""")]
    [InlineData("""{"value":[{"id":"mail-1","subject":"","body":{"contentType":"text","content":""},"receivedDateTime":"invalid"}]}""")]
    public async Task RunAsync_InvalidResponseFailsWithoutLeakingContent(string json)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var classifier = new RecordingClassifier();
        using var output = new StringWriter();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunAsync(output, CancellationToken.None));

        Assert.Empty(classifier.Messages);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [Fact]
    public async Task RunAsync_RejectsMoreThanFiveMessages()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.OK, CreateResponse(6)));
        var classifier = new RecordingClassifier();
        using var output = new StringWriter();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunAsync(output, CancellationToken.None));

        Assert.Empty(classifier.Messages);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task RunAsync_CancellationPreventsRequest()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(1));
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), new RecordingClassifier())
                .RunAsync(output, cancellation.Token));

        Assert.Empty(handler.Requests);
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-thumbprint")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ProbeCertificate_RejectsMissingOrMalformedThumbprint(string? thumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidOperationException>(() => ProbeCertificate.Load(thumbprint));
            return;
        }

        Assert.Throws<InvalidOperationException>(() => ProbeCertificate.Load(thumbprint));
    }

    [Fact]
    public async Task RunExamplesAsync_ReadsOnlyTwoPagesAndDoesNotPrintIdentifiersOrContent()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(50, includeNextLink: true));
        using var http = new HttpClient(handler);
        var classifier = new RecordingClassifier();
        using var output = new StringWriter();

        await new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunExamplesAsync(output, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/inbox/messages", handler.Requests[0].Uri.AbsolutePath);
        Assert.EndsWith("/deleteditems/messages", handler.Requests[1].Uri.AbsolutePath);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("$top=50", request.Uri.Query);
        });
        Assert.Equal(100, classifier.Messages.Count);
        Assert.Contains("Folder=inbox Read=50 ForwardSuggestions=50 ManualReview=0", output.ToString());
        Assert.Contains("Folder=deleteditems Read=50 ForwardSuggestions=50 ManualReview=0", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
        Assert.DoesNotContain("mail-0", output.ToString());
        Assert.DoesNotContain("test-token", output.ToString());
    }

    [Fact]
    public async Task RunExamplesAsync_CountsOverlappingKeywordCandidatesUsingRealClassifier()
    {
        var json = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new { id = "private-id", subject = "ordre faktura", body = new { contentType = "text", content = "ikke videresend" }, receivedDateTime = "2026-10-20T06:00:00Z" }
            }
        });
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.OK, json));
        var configuration = TestConfiguration.Create(new { });
        var classifier = new RuleBasedMailClassifier(configuration,
            new RoutingDecisionService(configuration, new StubDocumentReferenceService()));
        using var output = new StringWriter();

        await new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunExamplesAsync(output, CancellationToken.None);

        Assert.Contains("Category=Order ForwardSuggestions=0 ManualReview=1", output.ToString());
        Assert.Contains("Category=Invoice ForwardSuggestions=0 ManualReview=1", output.ToString());
        Assert.Contains("Category=Do not forward ForwardSuggestions=0 ManualReview=1", output.ToString());
        Assert.Contains("Category=General question ForwardSuggestions=0 ManualReview=0", output.ToString());
        Assert.DoesNotContain("private-id", output.ToString());
    }

    [Fact]
    public async Task RunExamplesAsync_RejectsMoreThanFiftyMessages()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.OK, CreateResponse(51)));
        using var output = new StringWriter();
        var classifier = new RecordingClassifier();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunExamplesAsync(output, CancellationToken.None));

        Assert.Empty(classifier.Messages);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task RunExamplesAsync_HttpFailureDoesNotReportCompletion()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.Forbidden, "PRIVATE"));
        using var output = new StringWriter();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), new RecordingClassifier()).RunExamplesAsync(output, CancellationToken.None));

        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task RunSummaryAsync_FiltersStartTimeAndOnlyEmitsCounts()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(2));
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        var classifier = new RecordingClassifier();
        var start = new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.Zero);

        await new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunSummaryAsync(output, start, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("receivedDateTime ge 2026-10-20T05:00:00.0000000Z", Uri.UnescapeDataString(request.Uri.Query));
        Assert.Contains("Read=2 ForwardSuggestions=2 ManualReview=0", output.ToString());
        Assert.DoesNotContain("mail-0", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
    }

    [Fact]
    public async Task RunSummaryAsync_RejectsMessagesBeforeApprovedStart()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.OK, CreateResponse(1)));
        using var output = new StringWriter();
        var classifier = new RecordingClassifier();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunSummaryAsync(
                output, new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), CancellationToken.None));

        Assert.Empty(classifier.Messages);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task AzurePilot_DefaultDisabledDoesNotRequestGraph()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(1));
        using var http = new HttpClient(handler);
        var configuration = TestConfiguration.Create(new { });
        using var worker = new AzureReadOnlyPilot(configuration,
            new GraphReadOnlyProbe(http, new StubCredential(), new RecordingClassifier()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReadOnlyPilot>.Instance);

        await worker.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AzurePilot_EnablingWithoutStartTimeFailsBeforeGraph()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AzurePilot:ReadingEnabled"] = "true" })
            .Build();
        var handler = new RecordingHandler(HttpStatusCode.OK, CreateResponse(1));
        using var http = new HttpClient(handler);
        using var worker = new AzureReadOnlyPilot(configuration,
            new GraphReadOnlyProbe(http, new StubCredential(), new RecordingClassifier()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureReadOnlyPilot>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            if (worker.ExecuteTask is not null)
            {
                await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RunSummaryAsync_RoutingPreviewShowsCatalogRecipientsButNoMailContentOrRawId()
    {
        var json = JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    id = "PRIVATE-ID",
                    subject = "PRIVATE-SUBJECT",
                    body = new { contentType = "text", content = "Medlemskab" },
                    receivedDateTime = "2026-10-20T06:00:00Z"
                }
            }
        });
        var handler = new RecordingHandler(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        var classifier = new CatalogMailClassifier(Path.Combine(AppContext.BaseDirectory, "TestData", "routing-catalog.json"));

        await new GraphReadOnlyProbe(http, new StubCredential(), classifier).RunSummaryAsync(
            output, new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), CancellationToken.None,
            includeRoutingDetails: true);

        var previewLine = output.ToString().Split(Environment.NewLine)
            .Single(line => line.StartsWith("RoutingPreview "));
        using var preview = JsonDocument.Parse(previewLine["RoutingPreview ".Length..]);
        var row = preview.RootElement;
        Assert.Equal(64, row.GetProperty("Reference").GetString()!.Length);
        Assert.Equal("RecipientSuggestion", row.GetProperty("Status").GetString());
        Assert.Equal(new[] { "indsamling@blind.dk", "medlemsservice@blind.dk" },
            row.GetProperty("CandidateTo").EnumerateArray().Select(item => item.GetString()));
        Assert.Empty(row.GetProperty("CandidateCc").EnumerateArray());
        Assert.Equal("shared-keywords", Assert.Single(row.GetProperty("Rules").EnumerateArray()).GetString());
        Assert.False(row.GetProperty("SendingEnabled").GetBoolean());
        Assert.DoesNotContain("PRIVATE", output.ToString());
        Assert.DoesNotContain("Medlemskab", output.ToString());
        Assert.DoesNotContain("test-token", output.ToString());
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    private static string CreateResponse(int count, bool includeNextLink = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["value"] = Enumerable.Range(0, count).Select(index => new
            {
                id = $"mail-{index}",
                subject = "PRIVATE-SUBJECT",
                body = new { contentType = "text", content = "PRIVATE-BODY" },
                receivedDateTime = "2026-10-20T06:00:00Z"
            }).ToArray(),
            ["@odata.nextLink"] = includeNextLink ? "https://graph.microsoft.com/should-not-be-called" : null
        });

    private sealed class RecordingClassifier : IMailClassifier
    {
        public List<EmailMessage> Messages { get; } = new();

        public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(new EmailDecision { Route = MailRoute.Forward, Reason = "PRIVATE-REASON" });
        }
    }

    private sealed class StubCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = Array.Empty<string>();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes = requestContext.Scopes;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Prefer, string? Authorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((request.Method, request.RequestUri!, string.Join(",", request.Headers.GetValues("Prefer")),
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
        }
    }
}

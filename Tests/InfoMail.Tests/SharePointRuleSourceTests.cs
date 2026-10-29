using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class SharePointRuleSourceTests
{
    [Fact]
    public async Task ReadAsync_DownloadRedirectNeverReceivesGraphToken()
    {
        using var doc = WordRuleTableTests.Create(("Arv og testamenter", "new@example.test"));
        var handler = new Handler((request, index) =>
        {
            if (index == 1)
            {
                Assert.Equal("Bearer fake-token", request.Headers.Authorization?.ToString());
                Assert.Contains("regeltabel.docx", request.RequestUri!.AbsoluteUri);
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://blindesamfund.sharepoint.com/download") }
                };
            }
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(doc.ToArray()) };
        });
        using var http = new HttpClient(handler);
        var snapshot = await new SharePointRuleSource(http, new Credential()).ReadAsync(CancellationToken.None);
        Assert.Single(snapshot.Rows);
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData("http://blindesamfund.sharepoint.com/download")]
    [InlineData("https://other.sharepoint.com/download")]
    [InlineData("https://example.test/download")]
    public async Task ReadAsync_RejectsUnexpectedRedirect(string location)
    {
        var handler = new Handler((request, index) => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri(location) }
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SharePointRuleSource(http, new Credential()).ReadAsync(CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ReadAsync_AccessAndMissingFileErrorsAreNotEmptyRules(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler((request, index) => new HttpResponseMessage(status)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new SharePointRuleSource(http, new Credential()).ReadAsync(CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task DailyClassifier_RestoresStateAndDoesNotCheckSharePointBeforeNextDay()
    {
        var catalog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "routing-catalog.json"));
        var next = DateTimeOffset.UtcNow.AddDays(1);
        var handler = new Handler((request, index) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["PartitionKey"] = "Rules", ["RowKey"] = "Current",
                ["Catalog"] = catalog, ["Hash"] = "saved-version",
                ["NextCheck"] = next.ToString("O"),
                ["NextCheck@odata.type"] = "Edm.DateTime"
            }))
        });
        using var http = new HttpClient(handler);
        var table = new TableClient(new Uri("https://state.example.test"), "PilotState", new Credential(),
            new TableClientOptions { Transport = new HttpClientTransport(http) });
        var sourceHandler = new Handler((request, index) => throw new InvalidOperationException("SharePoint must not be called yet."));
        using var sourceHttp = new HttpClient(sourceHandler);
        var classifier = new DailySharePointClassifier(new SharePointRuleSource(sourceHttp, new Credential()), table,
            NullLogger<DailySharePointClassifier>.Instance, catalog);

        await classifier.RefreshAsync(CancellationToken.None);
        await classifier.RefreshAsync(CancellationToken.None);
        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = "Testamente" }, CancellationToken.None);

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal(1, handler.Count);
        Assert.Equal(0, sourceHandler.Count);
    }

    [Fact]
    public async Task DailyClassifier_InvalidUpdatePreservesSavedRules()
    {
        var catalog = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "routing-catalog.json"));
        var state = new Dictionary<string, object>
        {
            ["PartitionKey"] = "Rules", ["RowKey"] = "Current",
            ["Catalog"] = catalog, ["Hash"] = "saved-version",
            ["NextCheck"] = DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),
            ["NextCheck@odata.type"] = "Edm.DateTime"
        };
        using var stateHttp = new HttpClient(new Handler((request, index) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(state))
        }));
        var table = new TableClient(new Uri("https://state.example.test"), "PilotState", new Credential(),
            new TableClientOptions { Transport = new HttpClientTransport(stateHttp) });
        using var bad = WordRuleTableTests.Create(("Arv og testamenter", "not-an-address"));
        using var sourceHttp = new HttpClient(new Handler((request, index) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bad.ToArray())
        }));
        var classifier = new DailySharePointClassifier(new SharePointRuleSource(sourceHttp, new Credential()), table,
            NullLogger<DailySharePointClassifier>.Instance, catalog);

        await Assert.ThrowsAsync<InvalidDataException>(() => classifier.RefreshAsync(CancellationToken.None));
        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = "Testamente" }, CancellationToken.None);
        Assert.Equal(new[] { "arvesager@blind.dk" }, result.Recipients);
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request, ++Count));
    }
}

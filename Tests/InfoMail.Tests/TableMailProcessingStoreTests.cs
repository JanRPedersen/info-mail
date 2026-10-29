using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class TableMailProcessingStoreTests
{
    [Fact]
    public async Task MissingControlMeansSendAndMoveAreDisabled()
    {
        using var http = new HttpClient(new Handler(request => new(HttpStatusCode.NotFound)));
        var controls = await Store(http).GetControlsAsync(CancellationToken.None);
        Assert.False(controls.Send);
        Assert.False(controls.Move);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ControlsAreReadFreshForEveryCall(bool send, bool move)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            return Json(new { PartitionKey = "Control", RowKey = "Forwarding", SendEnabled = send, MoveEnabled = move });
        }));
        var store = Store(http);
        Assert.Equal(new ProcessingControls(send, move), await store.GetControlsAsync(CancellationToken.None));
        Assert.Equal(new ProcessingControls(send, move), await store.GetControlsAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SaveRequiresExpectedETagAndConflictReturnsNoClaim()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("\"old-version\"", Assert.Single(request.Headers.GetValues("If-Match")));
            return new(HttpStatusCode.PreconditionFailed);
        }));
        var record = new ProcessingRecord("immutable-id", ProcessingStage.Sending, "\"old-version\"",
            new[] { "recipient@example.test" }, Array.Empty<string>(), "Attempt");

        Assert.Null(await Store(http).TrySaveAsync(record, record.Version, CancellationToken.None));
    }

    [Fact]
    public async Task SaveReturnsNewETagAndDoesNotStoreMailText()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("Sending", document.RootElement.GetProperty("Stage").GetString());
            Assert.False(document.RootElement.TryGetProperty("Body", out _));
            Assert.False(document.RootElement.TryGetProperty("Subject", out _));
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"new-version\"");
            return response;
        }));
        var record = new ProcessingRecord("immutable-id", ProcessingStage.Sending, "\"old-version\"",
            new[] { "recipient@example.test" }, Array.Empty<string>(), "Attempt");
        var saved = await Store(http).TrySaveAsync(record, record.Version, CancellationToken.None);
        Assert.Equal("\"new-version\"", saved!.Version);
    }

    [Fact]
    public async Task CompetingCreateReadsWinnerInsteadOfOverwriting()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            return calls switch
            {
                1 => new(HttpStatusCode.NotFound),
                2 => new(HttpStatusCode.Conflict),
                _ => Json(new
                {
                    PartitionKey = "Messages", RowKey = "reference", MessageId = "immutable-id",
                    Stage = "Sending", To = "[]", Cc = "[]", Reason = "Concurrent"
                })
            };
        }));
        var record = await Store(http).GetOrCreateAsync("immutable-id", CancellationToken.None);
        Assert.Equal(ProcessingStage.Sending, record.Stage);
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("null", typeof(InvalidDataException))]
    [InlineData("", typeof(JsonException))]
    public async Task InvalidPersistedRecipientsFailExplicitly(string to, Type errorType)
    {
        using var http = new HttpClient(new Handler(request => Json(new
        {
            PartitionKey = "Messages", RowKey = "reference", MessageId = "immutable-id",
            Stage = "Pending", To = to, Cc = "[]", Reason = "New"
        })));
        await Assert.ThrowsAsync(errorType, () => Store(http).GetOrCreateAsync("immutable-id", CancellationToken.None));
    }

    [Fact]
    public async Task GetExistingRestoresStageRecipientPlanAndImmutableId()
    {
        using var http = new HttpClient(new Handler(request => Json(new
        {
            PartitionKey = "Messages", RowKey = "reference", MessageId = "immutable-id",
            Stage = "Accepted", To = "[\"recipient@example.test\"]", Cc = "[]",
            Reason = "GraphAccepted202", Destination = ""
        })));
        var record = await Store(http).GetOrCreateAsync("immutable-id", CancellationToken.None);
        Assert.Equal(ProcessingStage.Accepted, record.Stage);
        Assert.Equal(new[] { "recipient@example.test" }, record.To);
        Assert.Equal("immutable-id", record.MessageId);
    }

    private static TableMailProcessingStore Store(HttpClient http)
    {
        var options = new TableClientOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        return new(new TableClient(new Uri("https://state.example.test"), "PilotState", new Credential(), options));
    }

    private static HttpResponseMessage Json(object body)
    {
        var json = JsonSerializer.SerializeToNode(body)!.AsObject();
        json["odata.etag"] = "W/\"saved\"";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json.ToJsonString())
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"saved\"");
        return response;
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}

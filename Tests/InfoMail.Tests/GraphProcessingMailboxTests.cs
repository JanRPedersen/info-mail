using System.Net;
using System.Text.Json;
using Azure.Core;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class GraphProcessingMailboxTests
{
    private const string Root = "https://graph.microsoft.com/v1.0/users/info%40blind.dk";
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadInboxAsync_FollowsPagesUsingImmutableIdsAndApprovedRange()
    {
        var handler = new Handler((request, number) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("IdType=\"ImmutableId\"", request.Headers.GetValues("Prefer"));
            Assert.Equal("Bearer test-token", request.Headers.Authorization?.ToString());
            return Json(HttpStatusCode.OK, Page(number == 1 ? $"{Root}/mailFolders/inbox/messages?$skiptoken=page2" : null,
                $"id-{number}"));
        });
        using var http = new HttpClient(handler);
        var mailbox = new GraphProcessingMailbox(http, new Credential());
        var messages = new List<InfoMail.Models.EmailMessage>();
        await foreach (var message in mailbox.ReadInboxAsync(Start, CancellationToken.None))
        {
            messages.Add(message);
        }
        Assert.Equal(new[] { "id-1", "id-2" }, messages.Select(message => message.Id));
        Assert.All(messages, message =>
        {
            Assert.Equal("sender@example.test", message.From);
            Assert.Equal("PRIVATE", message.Body);
            Assert.False(message.IsAutomatic);
        });
        var url = Uri.UnescapeDataString(handler.Urls[0]);
        Assert.Contains("receivedDateTime ge", url);
        Assert.Contains("receivedDateTime le", url);
    }

    [Theory]
    [InlineData("https://evil.example/v1.0/users/info%40blind.dk/messages")]
    [InlineData("https://graph.microsoft.com/v1.0/users/jap%40blind.dk/messages")]
    [InlineData("http://graph.microsoft.com/v1.0/users/info%40blind.dk/messages")]
    public async Task ReadInboxAsync_RejectsUntrustedPaginationBeforeSendingToken(string link)
    {
        var handler = new Handler((request, number) => Json(HttpStatusCode.OK, Page(link, "id-1")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var message in new GraphProcessingMailbox(http, new Credential()).ReadInboxAsync(Start, CancellationToken.None))
            {
            }
        });
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task ReadInboxAsync_RejectsPaginationCycles()
    {
        var link = $"{Root}/mailFolders/inbox/messages?$skiptoken=again";
        var handler = new Handler((request, number) => Json(HttpStatusCode.OK, Page(link, $"id-{number}")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var message in new GraphProcessingMailbox(http, new Credential()).ReadInboxAsync(Start, CancellationToken.None))
            {
            }
        });
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ReadInboxAsync_RetriesThrottledReadsNotWrites()
    {
        var handler = new Handler((request, number) => number == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) }
            }
            : Json(HttpStatusCode.OK, Page(null, "id-1")));
        using var http = new HttpClient(handler);
        await foreach (var message in new GraphProcessingMailbox(http, new Credential()).ReadInboxAsync(Start, CancellationToken.None))
        {
            Assert.Equal("id-1", message.Id);
        }
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-generated", true)]
    [InlineData("Auto-Submitted", "no", false)]
    [InlineData("X-Loop", "loop", true)]
    [InlineData("X-Auto-Response-Suppress", "All", true)]
    public async Task ReadInboxAsync_ExposesAutomaticMessageIndicators(string name, string value, bool expected)
    {
        var page = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(Page(null, "id-1"))!;
        page["value"]![0]!["internetMessageHeaders"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["value"] = value });
        using var http = new HttpClient(new Handler((request, number) => Json(HttpStatusCode.OK, page.ToJsonString())));
        await foreach (var message in new GraphProcessingMailbox(http, new Credential()).ReadInboxAsync(Start, CancellationToken.None))
        {
            Assert.Equal(expected, message.IsAutomatic);
        }
    }

    [Fact]
    public async Task ForwardAsync_SendsOneForwardWithAllToAndCcAndLoopMarker()
    {
        var handler = new Handler((request, number) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/messages/id-1/forward", request.RequestUri!.AbsolutePath);
            var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            Assert.StartsWith("[InfoMail-Forward:", document.RootElement.GetProperty("comment").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("message").GetProperty("toRecipients").GetArrayLength());
            Assert.Single(document.RootElement.GetProperty("message").GetProperty("ccRecipients").EnumerateArray());
            return new(HttpStatusCode.Accepted);
        });
        using var http = new HttpClient(handler);
        await new GraphProcessingMailbox(http, new Credential()).ForwardAsync("id-1",
            new[] { "one@example.test", "two@example.test" }, new[] { "cc@example.test" }, CancellationToken.None);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ForwardAsync_UnexpectedStatusNeverRetries(HttpStatusCode status)
    {
        var handler = new Handler((request, number) => new(status));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GraphProcessingMailbox(http, new Credential()).ForwardAsync("id-1", new[] { "one@example.test" },
                Array.Empty<string>(), CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task FindDestinationAsync_UsesExactNameAcrossPagesWithoutCreatingFolder()
    {
        var handler = new Handler((request, number) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/mailFolders/inbox/childFolders", request.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["value"] = new[] { new { id = $"folder-{number}", displayName = number == 1 ? "Other" : "Videresendt via regler" } },
                ["@odata.nextLink"] = number == 1 ? $"{Root}/mailFolders/inbox/childFolders?$skiptoken=more" : null
            }));
        });
        using var http = new HttpClient(handler);
        Assert.Equal("folder-2", await new GraphProcessingMailbox(http, new Credential()).FindDestinationAsync(CancellationToken.None));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task FindDestinationAsync_UsesRequestedDagensMeldingFolderName()
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                value = new[] { new { id = "daily-folder", displayName = "@Dagens melding" } }
            }));
        });
        using var http = new HttpClient(handler);

        Assert.Equal("daily-folder", await new GraphProcessingMailbox(http, new Credential())
            .FindDestinationAsync("@Dagens melding", CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task FindDestinationAsync_MissingOrDuplicateFolderFails(int count)
    {
        using var http = new HttpClient(new Handler((request, number) => Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { value = Enumerable.Range(0, count).Select(i => new { id = $"folder-{i}", displayName = "Videresendt via regler" }) }))));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GraphProcessingMailbox(http, new Credential()).FindDestinationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MoveAsync_OnlyAccepts201AndDoesNotRetry()
    {
        var handler = new Handler((request, number) => new(HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GraphProcessingMailbox(http, new Credential()).MoveAsync("id-1", "folder-1", CancellationToken.None));
        Assert.Equal(1, handler.Count);
    }

    private static string Page(string? next, string id) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["value"] = new[]
        {
            new { id, subject = "Subject", body = new { contentType = "text", content = "PRIVATE" },
                receivedDateTime = Start.AddMinutes(1).ToString("O"),
                from = new { emailAddress = new { address = "sender@example.test" } },
                internetMessageHeaders = Array.Empty<object>() }
        },
        ["@odata.nextLink"] = next
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json) };

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request, ++Count));
        }
    }
}

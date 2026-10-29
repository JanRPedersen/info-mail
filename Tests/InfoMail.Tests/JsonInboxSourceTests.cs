using System.Text.Json;
using InfoMail.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class JsonInboxSourceTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    [Fact]
    public async Task GetMessagesAsync_ReadsAllFieldsCaseInsensitively()
    {
        var path = _directory.FilePath("inbox.json");
        await File.WriteAllTextAsync(path, """
            [{"ID":"mail-1","FROM":"sender@example.test","SUBJECT":"Order","BODY":"Details","RECEIVEDAT":"2026-07-30T10:00:00+00:00"}]
            """);

        var message = Assert.Single(await CreateSource(path).GetMessagesAsync(CancellationToken.None));

        Assert.Equal("mail-1", message.Id);
        Assert.Equal("sender@example.test", message.From);
        Assert.Equal("Order", message.Subject);
        Assert.Equal("Details", message.Body);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero), message.ReceivedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMessagesAsync_MissingOrEmptyInboxReturnsNoMessages(bool exists)
    {
        var path = _directory.FilePath("inbox.json");
        if (exists)
        {
            await File.WriteAllTextAsync(path, "[]");
        }

        var result = await CreateSource(path).GetMessagesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMessagesAsync_InvalidJsonReportsError()
    {
        var path = _directory.FilePath("inbox.json");
        await File.WriteAllTextAsync(path, "not json");

        await Assert.ThrowsAsync<JsonException>(() => CreateSource(path).GetMessagesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MarkAsProcessedAsync_NextPollExcludesOnlyProcessedMessages()
    {
        var path = _directory.FilePath("inbox.json");
        await File.WriteAllTextAsync(path, """[{"id":"mail-1"},{"id":"mail-2"}]""");
        var source = CreateSource(path);
        Assert.Equal(2, (await source.GetMessagesAsync(CancellationToken.None)).Count);

        await source.MarkAsProcessedAsync("mail-1", CancellationToken.None);

        var remaining = await source.GetMessagesAsync(CancellationToken.None);
        Assert.Equal("mail-2", Assert.Single(remaining).Id);
    }

    [Fact]
    public async Task MarkAsProcessedAsync_RepeatedMarkingIsHarmlessAndDoesNotChangeInputFile()
    {
        var path = _directory.FilePath("inbox.json");
        const string json = """[{"id":"mail-1"}]""";
        await File.WriteAllTextAsync(path, json);
        var source = CreateSource(path);

        await source.MarkAsProcessedAsync("mail-1", CancellationToken.None);
        await source.MarkAsProcessedAsync("mail-1", CancellationToken.None);

        Assert.Empty(await source.GetMessagesAsync(CancellationToken.None));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task GetMessagesAsync_NewMessagesAreStillReadAfterPreviousMessagesAreProcessed()
    {
        var path = _directory.FilePath("inbox.json");
        await File.WriteAllTextAsync(path, """[{"id":"mail-1"}]""");
        var source = CreateSource(path);
        await source.GetMessagesAsync(CancellationToken.None);
        await source.MarkAsProcessedAsync("mail-1", CancellationToken.None);
        await File.WriteAllTextAsync(path, """[{"id":"mail-1"},{"id":"mail-2"}]""");

        var result = await source.GetMessagesAsync(CancellationToken.None);

        Assert.Equal("mail-2", Assert.Single(result).Id);
    }

    private static JsonInboxSource CreateSource(string path) =>
        new(TestConfiguration.Create(new { InboxDataPath = path }), NullLogger<JsonInboxSource>.Instance);

    public void Dispose() => _directory.Dispose();
}

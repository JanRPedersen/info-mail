using System.Text.Json;
using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class FileForwardingLogStoreTests : IDisposable
{
    private readonly TestDirectory _directory = new();

    [Fact]
    public async Task GetLogAsync_MissingLogReturnsNoEntries()
    {
        var store = CreateStore(_directory.FilePath("forwarded.json"));

        Assert.Empty(await store.GetLogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LogForwardAsync_CreatesDirectoryAndPersistsEveryField()
    {
        var path = _directory.FilePath(Path.Combine("nested", "forwarded.json"));
        var entry = new ForwardingLogEntry
        {
            Id = "log-1", MessageId = "mail-1", From = "sender@example.test",
            To = "orders@example.test", Subject = "Order", RuleName = "Orders",
            Reason = "Matched Orders", TargetFolder = "Orders",
            ForwardedAt = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero)
        };

        await CreateStore(path).LogForwardAsync(entry, CancellationToken.None);
        var saved = Assert.Single(await CreateStore(path).GetLogAsync(CancellationToken.None));

        Assert.Equal(entry.Id, saved.Id);
        Assert.Equal(entry.MessageId, saved.MessageId);
        Assert.Equal(entry.From, saved.From);
        Assert.Equal(entry.To, saved.To);
        Assert.Equal(entry.Subject, saved.Subject);
        Assert.Equal(entry.RuleName, saved.RuleName);
        Assert.Equal(entry.Reason, saved.Reason);
        Assert.Equal(entry.TargetFolder, saved.TargetFolder);
        Assert.Equal(entry.ForwardedAt, saved.ForwardedAt);
    }

    [Fact]
    public async Task LogForwardAsync_AppendsWithoutLosingExistingEntries()
    {
        var path = _directory.FilePath("forwarded.json");
        await CreateStore(path).LogForwardAsync(new ForwardingLogEntry { MessageId = "mail-1" }, CancellationToken.None);
        await CreateStore(path).LogForwardAsync(new ForwardingLogEntry { MessageId = "mail-2" }, CancellationToken.None);

        var entries = await CreateStore(path).GetLogAsync(CancellationToken.None);

        Assert.Equal(new[] { "mail-1", "mail-2" }, entries.Select(entry => entry.MessageId));
    }

    [Fact]
    public async Task GetLogAsync_InvalidJsonReportsError()
    {
        var path = _directory.FilePath("forwarded.json");
        await File.WriteAllTextAsync(path, "not json");

        await Assert.ThrowsAsync<JsonException>(() => CreateStore(path).GetLogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LogForwardAsync_DoesNotOverwriteCorruptLog()
    {
        var path = _directory.FilePath("forwarded.json");
        await File.WriteAllTextAsync(path, "not json");

        await Assert.ThrowsAsync<JsonException>(() =>
            CreateStore(path).LogForwardAsync(new ForwardingLogEntry(), CancellationToken.None));

        Assert.Equal("not json", await File.ReadAllTextAsync(path));
    }

    private static FileForwardingLogStore CreateStore(string path) =>
        new(TestConfiguration.Create(new { ForwardingLogPath = path }), NullLogger<FileForwardingLogStore>.Instance);

    public void Dispose() => _directory.Dispose();
}

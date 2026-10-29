using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class WorkerTests
{
    [Theory]
    [InlineData(MailRoute.Forward, "rule@example.test", "Rule folder", "default@example.test", "rule@example.test", "Rule folder")]
    [InlineData(MailRoute.Forward, null, null, "default@example.test", "default@example.test", "Default folder")]
    [InlineData(MailRoute.Forward, null, null, null, "mailbox@example.test", "Default folder")]
    [InlineData(MailRoute.ManualReview, null, null, null, null, null)]
    public async Task ExecuteAsync_LogsOnlyForwardedMessagesThenMarksProcessed(
        MailRoute route, string? recipient, string? folder, string? defaultRecipient,
        string? expectedRecipient, string? expectedFolder)
    {
        var message = new EmailMessage { Id = "mail-1", Subject = "Subject", From = "sender@example.test" };
        var inbox = new RecordingInbox(message);
        var log = new RecordingLog();
        var decision = new EmailDecision { Route = route, Recipient = recipient, TargetFolder = folder, Reason = "Test rule" };
        using var worker = CreateWorker(inbox, log, decision, defaultRecipient);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await inbox.Processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
        }

        Assert.Equal(new[] { "mail-1" }, inbox.ProcessedIds);
        if (route == MailRoute.ManualReview)
        {
            Assert.Empty(log.Entries);
        }
        else
        {
            var entry = Assert.Single(log.Entries);
            Assert.Equal(message.Id, entry.MessageId);
            Assert.Equal(message.Subject, entry.Subject);
            Assert.Equal(message.From, entry.From);
            Assert.Equal(expectedRecipient, entry.To);
            Assert.Equal(expectedFolder, entry.TargetFolder);
            Assert.Equal(decision.Reason, entry.RuleName);
            Assert.Equal(decision.Reason, entry.Reason);
            Assert.True(inbox.WasLoggedBeforeProcessed);
        }
    }

    [Fact]
    public async Task ExecuteAsync_LogFailureIsReportedAndMessageIsNotMarkedProcessed()
    {
        var inbox = new RecordingInbox(new EmailMessage { Id = "mail-1" });
        var log = new RecordingLog { Failure = new IOException("Cannot write log") };
        using var worker = CreateWorker(inbox, log, new EmailDecision { Route = MailRoute.Forward }, null);

        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await worker.StartAsync(CancellationToken.None);
            if (worker.ExecuteTask is not null)
            {
                await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        });

        Assert.Equal("Cannot write log", error.Message);
        Assert.Empty(inbox.ProcessedIds);
    }

    private static Worker CreateWorker(RecordingInbox inbox, RecordingLog log, EmailDecision decision, string? defaultRecipient)
    {
        inbox.Log = log;
        return new Worker(NullLogger<Worker>.Instance, inbox, new StubClassifier(decision),
            TestConfiguration.Create(new
            {
                MailboxAddress = "mailbox@example.test",
                ForwardRecipient = defaultRecipient,
                DefaultTargetFolder = "Default folder",
                PollingIntervalSeconds = 300
            }), log);
    }

    private sealed class StubClassifier(EmailDecision decision) : IMailClassifier
    {
        public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(decision);
    }

    private sealed class RecordingInbox(EmailMessage message) : IInboxSource
    {
        public TaskCompletionSource<bool> Processed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> ProcessedIds { get; } = new();
        public RecordingLog? Log { get; set; }
        public bool WasLoggedBeforeProcessed { get; private set; }

        public Task<IReadOnlyList<EmailMessage>> GetMessagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EmailMessage>>(new[] { message });

        public Task MarkAsProcessedAsync(string id, CancellationToken cancellationToken)
        {
            WasLoggedBeforeProcessed = Log?.Entries.Any(entry => entry.MessageId == id) == true;
            ProcessedIds.Add(id);
            Processed.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLog : IForwardingLogStore
    {
        public List<ForwardingLogEntry> Entries { get; } = new();
        public IOException? Failure { get; init; }

        public Task LogForwardAsync(ForwardingLogEntry entry, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ForwardingLogEntry>> GetLogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ForwardingLogEntry>>(Entries);
    }
}

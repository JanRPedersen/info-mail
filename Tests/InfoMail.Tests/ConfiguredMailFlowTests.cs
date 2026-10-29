using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class ConfiguredMailFlowTests
{
    [Theory]
    [InlineData("mail-001", MailRoute.Forward, "Order requests")]
    [InlineData("mail-002", MailRoute.Forward, "Invoice review")]
    [InlineData("mail-003", MailRoute.ManualReview, "General questions")]
    public async Task SampleMail_UsesExpectedConfiguredRule(string id, MailRoute expectedRoute, string expectedRule)
    {
        var configuration = LoadConfiguration();
        var inbox = new JsonInboxSource(TestConfiguration.Create(new
        {
            InboxDataPath = Path.Combine(AppContext.BaseDirectory, "TestData", "inbox-samples.json")
        }), NullLogger<JsonInboxSource>.Instance);
        var messages = await inbox.GetMessagesAsync(CancellationToken.None);
        Assert.Equal(3, messages.Count);

        var result = await CreateClassifier(configuration).ClassifyAsync(
            Assert.Single(messages, message => message.Id == id), CancellationToken.None);

        Assert.Equal(expectedRoute, result.Route);
        Assert.Contains(expectedRule, result.Reason);
        if (expectedRoute == MailRoute.Forward)
        {
            Assert.Equal(configuration["MailRobot:ForwardRecipient"], result.Recipient);
        }
    }

    [Theory]
    [InlineData("ordre", "IKKE VIDERESEND")]
    [InlineData("faktura", "holdes tilbage")]
    public async Task ConfiguredForwardRule_DoesNotOverrideExplicitHold(string subject, string body)
    {
        var result = await CreateClassifier(LoadConfiguration()).ClassifyAsync(
            new EmailMessage { Subject = subject, Body = body }, CancellationToken.None);

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Null(result.Recipient);
        Assert.Null(result.TargetFolder);
    }

    [Fact]
    public async Task Worker_RepeatedPollsDoNotLogSampleMessagesAgain()
    {
        using var directory = new TestDirectory();
        var logPath = directory.FilePath("forwarded.json");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(LoadConfiguration())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailRobot:InboxDataPath"] = Path.Combine(AppContext.BaseDirectory, "TestData", "inbox-samples.json"),
                ["MailRobot:ForwardingLogPath"] = logPath,
                ["MailRobot:PollingIntervalSeconds"] = "1"
            })
            .Build();
        var source = new JsonInboxSource(configuration, NullLogger<JsonInboxSource>.Instance);
        var inbox = new PollObserver(source);
        var log = new FileForwardingLogStore(configuration, NullLogger<FileForwardingLogStore>.Instance);
        using var worker = new Worker(NullLogger<Worker>.Instance, inbox, CreateClassifier(configuration), configuration, log);

        try
        {
            await worker.StartAsync(CancellationToken.None);
            await inbox.ThirdPoll.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
        }

        var entries = await log.GetLogAsync(CancellationToken.None);
        Assert.Equal(new[] { "mail-001", "mail-002" }, entries.Select(entry => entry.MessageId));
        Assert.Empty(await source.GetMessagesAsync(CancellationToken.None));
    }

    private static IConfiguration LoadConfiguration() => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "TestData", "appsettings.json"))
        .Build();

    private static RuleBasedMailClassifier CreateClassifier(IConfiguration configuration) =>
        new(configuration, new RoutingDecisionService(configuration, new StubDocumentReferenceService()));

    private sealed class PollObserver(IInboxSource inner) : IInboxSource
    {
        private int _polls;
        public TaskCompletionSource<bool> ThirdPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<EmailMessage>> GetMessagesAsync(CancellationToken cancellationToken)
        {
            var messages = await inner.GetMessagesAsync(cancellationToken);
            if (++_polls == 3)
            {
                ThirdPoll.TrySetResult(true);
            }

            return messages;
        }

        public Task MarkAsProcessedAsync(string id, CancellationToken cancellationToken) =>
            inner.MarkAsProcessedAsync(id, cancellationToken);
    }
}

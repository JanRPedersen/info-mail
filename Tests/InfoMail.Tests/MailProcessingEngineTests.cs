using System.Runtime.CompilerServices;
using System.Net;
using System.Text.Json;
using Azure.Core;
using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InfoMail.Tests;

public class MailProcessingEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static EmailMessage Message(string id = "immutable-1") => new()
    {
        Id = id, From = "sender@example.test", ReceivedAt = Start.AddMinutes(1), Subject = "Membership"
    };

    [Fact]
    public async Task DisabledEngineDoesNotReadOrClaim()
    {
        var mailbox = new Mailbox(Message());
        var store = new Store();
        await Engine(mailbox, store, enabled: false).RunAsync(CancellationToken.None);
        Assert.Equal(0, mailbox.Reads);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task EnabledEngineRequiresSeparateExplicitStartTime()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Processing:Enabled"] = "true", ["AzurePilot:StartTimeUtc"] = Start.ToString("O")
        }).Build();
        var mailbox = new Mailbox(Message());
        var engine = new MailProcessingEngine(mailbox, new Store(), new Classifier(), configuration,
            NullLogger<MailProcessingEngine>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(CancellationToken.None));
        Assert.Equal(0, mailbox.Reads);
    }

    [Fact]
    public async Task DryRunFinishesAllPagesWithoutClaimingStateOrWritingMail()
    {
        var mailbox = new Mailbox(Enumerable.Range(0, 125).Select(index => Message($"id-{index}")).ToArray());
        var store = new Store();
        await Engine(mailbox, store, send: false, move: true).RunAsync(CancellationToken.None);
        Assert.Equal(125, mailbox.Yielded);
        Assert.Empty(store.Records);
        Assert.Empty(mailbox.Writes);
    }

    [Fact]
    public async Task SendThenMoveIsPersistedAndRepeatedRunNeverResends()
    {
        var mailbox = new Mailbox(Message());
        var store = new Store();
        var engine = Engine(mailbox, store);
        await engine.RunAsync(CancellationToken.None);
        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "send", "move" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.Completed, store.Records["immutable-1"].Stage);
        Assert.Equal(new[] { "to@example.test", "other@example.test" }, mailbox.LastTo);
        Assert.Equal(new[] { "cc@example.test" }, mailbox.LastCc);
        Assert.Contains(ProcessingStage.Sending, store.History);
        Assert.Contains(ProcessingStage.Accepted, store.History);
        Assert.Contains(ProcessingStage.Moving, store.History);
    }

    [Fact]
    public async Task RestoredAcceptedStateMovesWithoutSendingAgain()
    {
        var store = new Store();
        store.Seed(ProcessingStage.Accepted);
        var mailbox = new Mailbox(Message());
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.Completed, store.Records["immutable-1"].Stage);
    }

    [Theory]
    [InlineData(ProcessingStage.Sending)]
    [InlineData(ProcessingStage.Moving)]
    public async Task InterruptedActionRequiresReconciliationNotRetry(ProcessingStage stage)
    {
        var store = new Store();
        store.Seed(stage);
        var mailbox = new Mailbox(Message());
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.NeedsReview, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task SendTimeoutNeverTriggersRetryOrMoveEvenAfterNewEngine()
    {
        var store = new Store();
        var mailbox = new Mailbox(Message()) { SendError = new TaskCanceledException() };
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Equal(ProcessingStage.Sending, store.Records["immutable-1"].Stage);
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "send" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.NeedsReview, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task SendFailureIsNotSuccessAndNeverMovesOriginal()
    {
        var store = new Store();
        var mailbox = new Mailbox(Message()) { SendError = new HttpRequestException("Forbidden") };
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "send" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.Sending, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task SaveFailureAfterSendNeverCausesAnotherSend()
    {
        var store = new Store { FailAcceptedSave = true };
        var mailbox = new Mailbox(Message());
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        store.FailAcceptedSave = false;
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "send" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.NeedsReview, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task MoveTimeoutNeverResendsOrBlindlyMovesAgain()
    {
        var store = new Store();
        var mailbox = new Mailbox(Message()) { MoveError = new HttpRequestException("Timeout") };
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "send", "move" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.NeedsReview, store.Records["immutable-1"].Stage);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ConfigurationAndPersistentControlMustBothAllowMove(bool configured, bool stored)
    {
        var store = new Store { Controls = new(true, stored) };
        var mailbox = new Mailbox(Message());
        await Engine(mailbox, store, move: configured).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "send" }, mailbox.Writes);
        Assert.Equal(ProcessingStage.Accepted, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task PersistentSendKillSwitchPreventsSend()
    {
        var mailbox = new Mailbox(Message());
        var store = new Store { Controls = new(false, true) };
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.Pending, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task KillSwitchClosedAfterClaimPreventsSendAndRestoresPending()
    {
        var mailbox = new Mailbox(Message());
        var store = new Store { CloseAfterFirstCheck = true };
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.Pending, store.Records["immutable-1"].Stage);
    }

    [Fact]
    public async Task ClaimConflictPreventsExternalAction()
    {
        var mailbox = new Mailbox(Message());
        var store = new Store { RejectClaim = true };
        await Engine(mailbox, store).RunAsync(CancellationToken.None);
        Assert.Empty(mailbox.Writes);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("marker")]
    [InlineData("automatic")]
    [InlineData("internal-sender")]
    [InlineData("invalid-recipient")]
    [InlineData("self-recipient")]
    [InlineData("manual")]
    [InlineData("empty-recipient")]
    public async Task UnsafeOrUnresolvedMessagesStayManual(string kind)
    {
        var message = Message();
        var classifier = new Classifier();
        switch (kind)
        {
            case "self": message.From = "INFO@blind.dk"; break;
            case "marker": message.Body = "[InfoMail-Forward:previous]"; break;
            case "automatic": message.IsAutomatic = true; break;
            case "internal-sender": message.From = "employee@blind.dk"; break;
            case "invalid-recipient": classifier.To = new[] { "no-address" }; break;
            case "self-recipient": classifier.To = new[] { "INFO@blind.dk" }; break;
            case "manual": classifier.Route = MailRoute.ManualReview; break;
            case "empty-recipient": classifier.To = Array.Empty<string>(); break;
        }
        var mailbox = new Mailbox(message);
        var store = new Store();
        await Engine(mailbox, store, classifier: classifier).RunAsync(CancellationToken.None);
        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.ManualReview, store.Records[message.Id].Stage);
        if (kind == "internal-sender")
            Assert.Equal("InternalSender", store.Records[message.Id].Reason);
    }

    [Fact]
    public async Task ScanFailureOnLaterPagePreventsAllWrites()
    {
        var mailbox = new Mailbox(Message()) { FailAtEnd = true };
        var store = new Store();
        await Assert.ThrowsAsync<HttpRequestException>(() => Engine(mailbox, store).RunAsync(CancellationToken.None));
        Assert.Empty(mailbox.Writes);
        Assert.Empty(store.Records);
    }

    [Fact]
    public async Task OldMessageAbortsBeforeAnyClaims()
    {
        var old = Message();
        old.ReceivedAt = Start.AddSeconds(-1);
        var mailbox = new Mailbox(Message("new"), old);
        var store = new Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine(mailbox, store).RunAsync(CancellationToken.None));
        Assert.Empty(store.Records);
        Assert.Empty(mailbox.Writes);
    }

    [Fact]
    public async Task MissingDestinationStopsBeforeForwardAttempt()
    {
        var store = new Store();
        var mailbox = new Mailbox(Message()) { DestinationError = new InvalidDataException("Missing folder") };
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine(mailbox, store).RunAsync(CancellationToken.None));
        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.Pending, store.Records["immutable-1"].Stage);
    }

    [Theory]
    [InlineData("Vi sygemelder hermed vores kollega.", "Vi sygemelder hermed vores kollega.")]
    [InlineData("Sygemelding", "Sygemelding")]
    [InlineData("Medarbejderen er sygefraværende.", "Der er sygefravær")]
    public async Task SickLeaveIsMovedToDagensMeldingWithoutForwarding(string subject, string body)
    {
        var message = Message();
        message.Subject = subject;
        message.Body = body;
        var mailbox = new Mailbox(message);
        var store = new Store();

        await Engine(mailbox, store).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(AbsenceRouting.DestinationFolder, mailbox.LastDestinationFolder);
        Assert.Equal(ProcessingStage.Completed, store.Records[message.Id].Stage);
        Assert.Equal("AbsenceMoveOnlyConfirmed", store.Records[message.Id].Reason);
        Assert.Empty(store.Records[message.Id].To);
    }

    [Fact]
    public async Task SickLeavePreviouslyInManualReviewIsMovedToDagensMelding()
    {
        var message = Message();
        message.Subject = "Sygemelding for i dag";
        var mailbox = new Mailbox(message);
        var store = new Store();
        store.Seed(ProcessingStage.ManualReview);

        await Engine(mailbox, store).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(AbsenceRouting.DestinationFolder, mailbox.LastDestinationFolder);
        Assert.Equal(ProcessingStage.Completed, store.Records[message.Id].Stage);
    }

    [Fact]
    public async Task VacationPreviouslyInManualReviewIsMovedToDagensMelding()
    {
        var message = Message();
        message.Subject = "Ferieønske";
        var mailbox = new Mailbox(message);
        var store = new Store();
        store.Seed(ProcessingStage.ManualReview);

        await Engine(mailbox, store).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(AbsenceRouting.DestinationFolder, mailbox.LastDestinationFolder);
        Assert.Equal(ProcessingStage.Completed, store.Records[message.Id].Stage);
        Assert.Equal("AbsenceMoveOnlyConfirmed", store.Records[message.Id].Reason);
        Assert.Empty(store.Records[message.Id].To);
    }

    [Fact]
    public async Task SickLeaveMoveGateClosedDoesNotForwardOrMove()
    {
        var message = Message();
        message.Subject = "Sygemelding";
        var mailbox = new Mailbox(message);
        var store = new Store { Controls = new(true, false) };

        await Engine(mailbox, store, move: false).RunAsync(CancellationToken.None);

        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.ManualReview, store.Records[message.Id].Stage);
        Assert.Equal("AbsenceMoveGateClosed", store.Records[message.Id].Reason);
    }

    [Theory]
    [InlineData("Ferieønske", "Jeg ønsker ferie i uge 32.")]
    [InlineData("Fraværsmeddelelse", "Jeg er på orlov fra mandag.")]
    [InlineData("Orlov", "Barsel starter næste måned.")]
    [InlineData("Afspadsering", "Jeg holder omsorgsdag.")]
    public async Task AbsenceMessageIsMovedToDagensMeldingInsteadOfForwardedToPayroll(string subject, string body)
    {
        var message = Message();
        message.Subject = subject;
        message.Body = body;
        var mailbox = new Mailbox(message);
        var store = new Store();
        var classifier = new Classifier
        {
            To = new[] { "loenadministration@blind.dk", "other@example.test" },
            Cc = new[] { "copy@example.test" }
        };

        await Engine(mailbox, store, classifier: classifier).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(AbsenceRouting.DestinationFolder, mailbox.LastDestinationFolder);
        Assert.Equal(ProcessingStage.Completed, store.Records[message.Id].Stage);
        Assert.Equal("AbsenceMoveOnlyConfirmed", store.Records[message.Id].Reason);
        Assert.Empty(store.Records[message.Id].To);
    }

    [Fact]
    public async Task AbsenceMessageWithNonPayrollRecipientIsAlsoMovedOnly()
    {
        var message = Message();
        message.Subject = "Ferieplan fra medlem";
        var mailbox = new Mailbox(message);
        var store = new Store();
        var classifier = new Classifier { To = new[] { "member-services@blind.dk" } };

        await Engine(mailbox, store, classifier: classifier).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(AbsenceRouting.DestinationFolder, mailbox.LastDestinationFolder);
        Assert.Empty(store.Records[message.Id].To);
    }

    [Fact]
    public async Task AbsenceAiCandidateIsSuppressedAndMailMovedToDagensMelding()
    {
        var message = Message();
        message.Subject = "Ferieønske";
        var mailbox = new Mailbox(message);
        var processingStore = new Store();
        var shadowStore = new ShadowStore(
            """{"Topics":[{"Id":"payroll","To":["loenadministration@blind.dk"],"Unresolved":[]}],"KeywordRules":[],"EmployeeFeedbackExamples":[]}""");
        var config = AiCandidateConfiguration();
        var handler = new ShadowHandler(mailbox, HttpStatusCode.OK)
        {
            Answer = """{"route":"Forward","ruleIds":["payroll"],"recipientCandidates":["loenadministration@blind.dk"]}"""
        };
        using var http = new HttpClient(handler);
        var shadow = new AiShadowObserver(http, new ShadowCredential(), shadowStore, config,
            NullLogger<AiShadowObserver>.Instance);
        var engine = new MailProcessingEngine(mailbox, processingStore,
            new Classifier { To = new[] { "loenadministration@blind.dk" } }, config,
            NullLogger<MailProcessingEngine>.Instance, shadow);

        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "move" }, mailbox.Writes);
        Assert.Equal(AbsenceRouting.DestinationFolder, mailbox.LastDestinationFolder);
        Assert.Equal(ProcessingStage.Completed, processingStore.Records[message.Id].Stage);
        Assert.Equal("AbsenceMoveOnlyConfirmed", processingStore.Records[message.Id].Reason);
        Assert.Equal("ManualReview", shadowStore.LastResult!.Route);
        Assert.Empty(shadowStore.LastResult.CandidateTo ?? Array.Empty<string>());
    }

    [Fact]
    public async Task SnapshotCountLimitStopsBeforeAnyClaimsOrWrites()
    {
        var mailbox = new Mailbox(Enumerable.Range(0, 10_001).Select(index => Message($"id-{index}")).ToArray());
        var store = new Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine(mailbox, store).RunAsync(CancellationToken.None));
        Assert.Empty(store.Records);
        Assert.Empty(mailbox.Writes);
    }

    [Fact]
    public async Task SnapshotTextLimitStopsBeforeAnyClaimsOrWrites()
    {
        var message = Message();
        message.Body = new string('x', 32 * 1024 * 1024);
        var mailbox = new Mailbox(message);
        var store = new Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine(mailbox, store).RunAsync(CancellationToken.None));
        Assert.Empty(store.Records);
        Assert.Empty(mailbox.Writes);
    }

    [Fact]
    public async Task DuplicateIdsInSnapshotAreProcessedOnce()
    {
        var mailbox = new Mailbox(Message(), Message());
        await Engine(mailbox, new Store()).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { "send", "move" }, mailbox.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AllNewShadowCoversMoreThanFiveBeforeMailWritesWithoutControllingThem(HttpStatusCode status)
    {
        var mailbox = new Mailbox(Enumerable.Range(0, 7).Select(index => Message("id-" + index)).ToArray());
        var processingStore = new Store();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Processing:Enabled"] = "true", ["Processing:StartTimeUtc"] = Start.ToString("O"),
            ["Processing:SendEnabled"] = "true", ["Processing:MoveEnabled"] = "true",
            ["AiShadow:Coverage"] = "AllNew", ["AiShadow:DataProcessingApproved"] = "true",
            ["AiShadow:StartTimeUtc"] = Start.ToString("O"), ["AiShadow:Deployment"] = "gpt-5.4-mini",
            ["AiShadow:Endpoint"] = "https://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions"
        }).Build();
        var handler = new ShadowHandler(mailbox, status);
        using var http = new HttpClient(handler);
        var shadow = new AiShadowObserver(http, new ShadowCredential(), new ShadowStore(), config,
            NullLogger<AiShadowObserver>.Instance);
        var engine = new MailProcessingEngine(mailbox, processingStore, new Classifier(), config,
            NullLogger<MailProcessingEngine>.Instance, shadow);

        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(7, handler.Calls);
        Assert.Equal(14, mailbox.Writes.Count);
        Assert.All(processingStore.Records.Values, record => Assert.Equal(ProcessingStage.Completed, record.Stage));
        Assert.Equal(new[] { "to@example.test", "other@example.test" }, mailbox.LastTo);
    }

    [Fact]
    public async Task ApprovedAiCandidateForwardsAndReopensPreviouslyManualReview()
    {
        var mailbox = new Mailbox(Message());
        var processingStore = new Store();
        processingStore.Seed(ProcessingStage.ManualReview);
        var config = AiCandidateConfiguration();
        var handler = new ShadowHandler(mailbox, HttpStatusCode.OK)
        {
            Answer = """{"route":"ManualReview","ruleIds":[],"recipientCandidates":["team@blind.dk"]}"""
        };
        using var http = new HttpClient(handler);
        var shadow = new AiShadowObserver(http, new ShadowCredential(), new ShadowStore(), config,
            NullLogger<AiShadowObserver>.Instance);
        var engine = new MailProcessingEngine(mailbox, processingStore, new Classifier(), config,
            NullLogger<MailProcessingEngine>.Instance, shadow);

        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "send", "move" }, mailbox.Writes);
        Assert.Equal(new[] { "team@blind.dk" }, mailbox.LastTo);
        Assert.Equal(ProcessingStage.Completed, processingStore.Records[Message().Id].Stage);
        Assert.Contains("AiCandidateForward", processingStore.Reasons);
    }

    [Fact]
    public async Task InternalSenderCannotBeForwardedByAiCandidate()
    {
        var message = Message();
        message.From = "employee@blind.dk";
        var mailbox = new Mailbox(message);
        var processingStore = new Store();
        var config = AiCandidateConfiguration();
        var handler = new ShadowHandler(mailbox, HttpStatusCode.OK)
        {
            Answer = """{"route":"Forward","ruleIds":[],"recipientCandidates":["team@blind.dk"]}"""
        };
        using var http = new HttpClient(handler);
        var shadow = new AiShadowObserver(http, new ShadowCredential(), new ShadowStore(), config,
            NullLogger<AiShadowObserver>.Instance);
        var engine = new MailProcessingEngine(mailbox, processingStore, new Classifier(), config,
            NullLogger<MailProcessingEngine>.Instance, shadow);

        await engine.RunAsync(CancellationToken.None);

        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.ManualReview, processingStore.Records[message.Id].Stage);
        Assert.Equal("InternalSender", processingStore.Records[message.Id].Reason);
    }

    [Theory]
    [InlineData("magazine-submissions")]
    [InlineData("letters-to-editor")]
    [InlineData("missing-magazine")]
    public async Task MembershipMagazineTopicsForwardOnlyToMagazineAndReopenManualReview(string topicId)
    {
        var message = Message();
        var mailbox = new Mailbox(message);
        var processingStore = new Store();
        processingStore.Seed(ProcessingStage.ManualReview);
        var classifier = new Classifier
        {
            Route = MailRoute.ManualReview,
            To = new[] { "medlemsbladet@blind.dk", "medlemsservice@blind.dk" },
            Cc = new[] { "another-team@blind.dk" },
            MatchedRules = new() { topicId, "member-administration" }
        };

        await Engine(mailbox, processingStore, classifier: classifier).RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "send", "move" }, mailbox.Writes);
        Assert.Equal(new[] { "medlemsbladet@blind.dk" }, mailbox.LastTo);
        Assert.Empty(mailbox.LastCc!);
        Assert.Equal(ProcessingStage.Completed, processingStore.Records[message.Id].Stage);
        Assert.Contains("MembershipMagazineExclusive", processingStore.Reasons);
    }

    [Fact]
    public async Task MembershipMagazineTopicOverridesAdditionalAiCandidates()
    {
        var message = Message();
        var mailbox = new Mailbox(message);
        var processingStore = new Store();
        var config = AiCandidateConfiguration();
        var handler = new ShadowHandler(mailbox, HttpStatusCode.OK)
        {
            Answer = """{"route":"Forward","ruleIds":[],"recipientCandidates":["team@blind.dk"]}"""
        };
        using var http = new HttpClient(handler);
        var shadow = new AiShadowObserver(http, new ShadowCredential(), new ShadowStore(), config,
            NullLogger<AiShadowObserver>.Instance);
        var classifier = new Classifier
        {
            MatchedRules = new() { "magazine-submissions", "member-administration" }
        };
        var engine = new MailProcessingEngine(mailbox, processingStore, classifier, config,
            NullLogger<MailProcessingEngine>.Instance, shadow);

        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "send", "move" }, mailbox.Writes);
        Assert.Equal(new[] { "medlemsbladet@blind.dk" }, mailbox.LastTo);
        Assert.Empty(mailbox.LastCc!);
    }

    [Fact]
    public async Task ExplicitHoldCannotBeOverriddenByAiRecipientCandidate()
    {
        var message = Message();
        message.Body = "Ikke videresend denne mail.";
        var mailbox = new Mailbox(message);
        var processingStore = new Store();
        processingStore.Seed(ProcessingStage.ManualReview);
        var config = AiCandidateConfiguration();
        var handler = new ShadowHandler(mailbox, HttpStatusCode.OK)
        {
            Answer = """{"route":"Forward","ruleIds":[],"recipientCandidates":["team@blind.dk"]}"""
        };
        using var http = new HttpClient(handler);
        var shadow = new AiShadowObserver(http, new ShadowCredential(), new ShadowStore(), config,
            NullLogger<AiShadowObserver>.Instance);
        var engine = new MailProcessingEngine(mailbox, processingStore, new Classifier(), config,
            NullLogger<MailProcessingEngine>.Instance, shadow);

        await engine.RunAsync(CancellationToken.None);

        Assert.Empty(mailbox.Writes);
        Assert.Equal(ProcessingStage.ManualReview, processingStore.Records[message.Id].Stage);
    }

    private sealed class ShadowHandler(Mailbox mailbox, HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Answer { get; init; } = """{"route":"ManualReview","ruleIds":[],"recipientCandidates":[]}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Calls * 2, mailbox.Writes.Count);
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = Answer } } }
                }))
            });
        }
    }

    private sealed class ShadowCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ShadowStore(string? catalog = null) : IAiShadowStore
    {
        public ShadowResult? LastResult { get; private set; }
        public Task<ShadowRules> GetRulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ShadowRules(catalog ??
                """{"Topics":[{"Id":"donors"}],"KeywordRules":[],"EmployeeFeedbackExamples":[{"ActualRecipients":["team@blind.dk"]}]}""", "test"));
        public Task<bool> TryClaimAsync(string reference, int dailyLimit, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteAsync(string reference, ShadowResult result, CancellationToken cancellationToken)
        {
            LastResult = result;
            return Task.CompletedTask;
        }
        public Task FailAsync(string reference, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static MailProcessingEngine Engine(Mailbox mailbox, Store store, bool enabled = true, bool send = true,
        bool move = true, Classifier? classifier = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Processing:Enabled"] = enabled.ToString(), ["Processing:StartTimeUtc"] = Start.ToString("O"),
            ["Processing:SendEnabled"] = send.ToString(), ["Processing:MoveEnabled"] = move.ToString()
        }).Build();
        return new(mailbox, store, classifier ?? new Classifier(), config, NullLogger<MailProcessingEngine>.Instance);
    }

    private static IConfiguration AiCandidateConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Processing:Enabled"] = "true", ["Processing:StartTimeUtc"] = Start.ToString("O"),
            ["Processing:SendEnabled"] = "true", ["Processing:MoveEnabled"] = "true",
            ["Processing:UseAiRecipientCandidates"] = "true",
            ["AiShadow:Enabled"] = "true", ["AiShadow:Coverage"] = "AllNew",
            ["AiShadow:IncludeSharePointKnowledge"] = "true", ["AiShadow:ComparisonListEnabled"] = "true",
            ["AiShadow:DataProcessingApproved"] = "true", ["AiShadow:StartTimeUtc"] = Start.ToString("O"),
            ["AiShadow:Deployment"] = "gpt-5.4-mini",
            ["AiShadow:Endpoint"] = "https://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions"
        }).Build();

    private sealed class Classifier : IMailClassifier
    {
        public MailRoute Route { get; set; } = MailRoute.Forward;
        public string[] To { get; set; } = new[] { "to@example.test", "other@example.test" };
        public string[] Cc { get; set; } = new[] { "cc@example.test" };
        public List<string> MatchedRules { get; set; } = new();
        public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(new EmailDecision
            {
                Route = Route, Recipients = To.ToList(),
                CcRecipients = Cc.ToList(), Reason = "Test",
                MatchedRules = MatchedRules.ToList()
            });
    }

    private sealed class Mailbox(params EmailMessage[] messages) : IProcessingMailbox
    {
        public int Reads { get; private set; }
        public int Yielded { get; private set; }
        public bool FailAtEnd { get; init; }
        public Exception? SendError { get; init; }
        public Exception? MoveError { get; init; }
        public Exception? DestinationError { get; init; }
        public List<string> Writes { get; } = new();
        public string[]? LastTo { get; private set; }
        public string[]? LastCc { get; private set; }
        public string? LastDestinationFolder { get; private set; }
        public async IAsyncEnumerable<EmailMessage> ReadInboxAsync(DateTimeOffset start,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Reads++;
            foreach (var message in messages)
            {
                Yielded++;
                yield return message;
            }
            await Task.CompletedTask;
            if (FailAtEnd)
            {
                throw new HttpRequestException("Next page failed");
            }
        }
        public Task ForwardAsync(string id, string[] to, string[] cc, CancellationToken cancellationToken)
        {
            Assert.Equal(messages.Length, Yielded);
            Writes.Add("send"); LastTo = to; LastCc = cc;
            return SendError is null ? Task.CompletedTask : Task.FromException(SendError);
        }
        public Task<string> FindDestinationAsync(CancellationToken cancellationToken) =>
            FindDestinationAsync("Videresendt via regler", cancellationToken);
        public Task<string> FindDestinationAsync(string folderName, CancellationToken cancellationToken)
        {
            LastDestinationFolder = folderName;
            return DestinationError is null
                ? Task.FromResult("folder-id") : Task.FromException<string>(DestinationError);
        }
        public Task MoveAsync(string id, string destination, CancellationToken cancellationToken)
        {
            Writes.Add("move");
            return MoveError is null ? Task.CompletedTask : Task.FromException(MoveError);
        }
    }

    private sealed class Store : IMailProcessingStore
    {
        public Dictionary<string, ProcessingRecord> Records { get; } = new();
        public List<ProcessingStage> History { get; } = new();
        public List<string> Reasons { get; } = new();
        public ProcessingControls Controls { get; init; } = new(true, true);
        public bool RejectClaim { get; init; }
        public bool FailAcceptedSave { get; set; }
        public bool CloseAfterFirstCheck { get; init; }
        private int _checks;
        public void Seed(ProcessingStage stage) =>
            Records["immutable-1"] = new("immutable-1", stage, "0", new[] { "to@example.test" }, Array.Empty<string>(), "Restored");
        public Task<ProcessingRecord> GetOrCreateAsync(string messageId, CancellationToken cancellationToken)
        {
            if (!Records.ContainsKey(messageId))
            {
                Records[messageId] = new(messageId, ProcessingStage.Pending, "0", Array.Empty<string>(), Array.Empty<string>(), "New");
            }
            return Task.FromResult(Records[messageId]);
        }
        public Task<ProcessingRecord?> TrySaveAsync(ProcessingRecord record, string expectedVersion, CancellationToken cancellationToken)
        {
            if (FailAcceptedSave && record.Stage == ProcessingStage.Accepted)
            {
                throw new Azure.RequestFailedException(503, "Storage unavailable");
            }
            if (Records[record.MessageId].Version != expectedVersion || (RejectClaim && record.Stage == ProcessingStage.Sending))
            {
                return Task.FromResult<ProcessingRecord?>(null);
            }
            var updated = record with { Version = (int.Parse(expectedVersion) + 1).ToString() };
            Records[record.MessageId] = updated;
            History.Add(updated.Stage);
            Reasons.Add(updated.Reason);
            return Task.FromResult<ProcessingRecord?>(updated);
        }
        public Task<ProcessingControls> GetControlsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CloseAfterFirstCheck && ++_checks > 1 ? new(false, false) : Controls);
    }
}

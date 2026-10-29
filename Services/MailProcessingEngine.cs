using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using InfoMail.Models;

namespace InfoMail.Services;

public sealed class MailProcessingEngine(
    IProcessingMailbox mailbox, IMailProcessingStore store, IMailClassifier classifier,
    IConfiguration configuration, ILogger<MailProcessingEngine> logger, AiShadowObserver? shadow = null)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Processing:Enabled", false))
        {
            logger.LogInformation("Processing engine disabled. Sending=false. Moving=false.");
            return;
        }
        if (!DateTimeOffset.TryParseExact(configuration["Processing:StartTimeUtc"], "O",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start))
        {
            throw new InvalidOperationException("Processing requires its own explicitly approved StartTimeUtc.");
        }
        var sendEnabled = configuration.GetValue("Processing:SendEnabled", false);
        var moveEnabled = configuration.GetValue("Processing:MoveEnabled", false);
        var useAiCandidates = configuration.GetValue("Processing:UseAiRecipientCandidates", false);
        if (useAiCandidates && (shadow is null || configuration["AiShadow:Coverage"] != "AllNew" ||
            !configuration.GetValue("AiShadow:Enabled", false) ||
            !configuration.GetValue("AiShadow:IncludeSharePointKnowledge", false) ||
            !configuration.GetValue("AiShadow:ComparisonListEnabled", false)))
        {
            throw new InvalidOperationException("AI candidate forwarding requires AllNew shadow evaluation, SharePoint knowledge and the comparison list.");
        }
        var messages = new List<EmailMessage>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long characters = 0;
        await foreach (var message in mailbox.ReadInboxAsync(start, cancellationToken))
        {
            if (message.ReceivedAt < start)
            {
                throw new InvalidDataException("Mailbox returned a message before activation.");
            }
            if (ids.Add(message.Id))
            {
                messages.Add(message);
                characters += (long)message.Subject.Length + message.Body.Length;
            }
            if (messages.Count > 10_000 || characters > 32 * 1024 * 1024)
            {
                throw new InvalidDataException("Scan snapshot exceeds its safety limit; no mailbox writes have started.");
            }
        }
        // Finish pagination before moving anything: mutating a folder mid-scan can skip rows.
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EmailDecision? baseline = null;
            EmailDecision? aiCandidate = null;
            if (shadow is not null && configuration["AiShadow:Coverage"] == "AllNew")
            {
                baseline = await classifier.ClassifyAsync(message, cancellationToken);
                if (IsLoop(message))
                {
                    baseline = new EmailDecision
                    {
                        Route = MailRoute.ManualReview, MatchedRules = baseline.MatchedRules,
                        Reason = IsInternalSender(message) ? "InternalSender" : "LoopOrAutomaticMessage"
                    };
                }
                var observation = await shadow.EvaluateAsync(message, baseline, cancellationToken);
                if (useAiCandidates)
                    aiCandidate = observation.CandidateDecision;
            }
            if (!sendEnabled)
            {
                var preview = baseline ?? await classifier.ClassifyAsync(message, cancellationToken);
                logger.LogInformation("Processing dry-run Reference={Reference} Route={Route}. No state claimed; no mailbox changes.",
                    Reference(message.Id), IsLoop(message) ? MailRoute.ManualReview : preview.Route);
                continue;
            }
            await ProcessAsync(message, moveEnabled, baseline, aiCandidate, cancellationToken);
        }
        logger.LogInformation("Processing scan completed. Read={Count} SendConfigured={Send} MoveConfigured={Move}.",
            messages.Count, sendEnabled, moveEnabled);
    }

    private async Task ProcessAsync(EmailMessage message, bool moveEnabled, EmailDecision? baseline,
        EmailDecision? aiCandidate, CancellationToken cancellationToken)
    {
        var state = await store.GetOrCreateAsync(message.Id, cancellationToken);
        var absence = AbsenceRouting.IsAbsence(message.Subject, message.Body);
        if (state.Stage is ProcessingStage.Sending or ProcessingStage.Moving)
        {
            // The previous process may have completed the external effect before losing its response.
            await SaveAsync(state with { Stage = ProcessingStage.NeedsReview, Reason = "InterruptedExternalAction" }, cancellationToken);
            logger.LogError("Processing NeedsReview Reference={Reference}. Interrupted external action; never automatically resend or repeat move.",
                Reference(message.Id));
            return;
        }
        if (state.Stage == ProcessingStage.ManualReview && baseline is null)
            baseline = await classifier.ClassifyAsync(message, cancellationToken);
        if (state.Stage == ProcessingStage.ManualReview &&
            (absence ||
             (aiCandidate is not null || MembershipMagazineRouting.Applies(baseline)) &&
             !IsLoop(message) && !RoutingDecisionService.ContainsNoForwardInstruction(message.Subject + " " + message.Body)))
        {
            var reopened = await SaveAsync(state with { Stage = ProcessingStage.Pending, Reason = "AiCandidateRecheck" },
                cancellationToken);
            if (reopened is null)
                return;
            state = reopened;
        }
        if (state.Stage is ProcessingStage.ManualReview or ProcessingStage.NeedsReview or ProcessingStage.Completed)
        {
            return;
        }
        if (state.Stage == ProcessingStage.Pending)
        {
            if (absence)
            {
                await MoveOnlyAsync(message, state, moveEnabled, cancellationToken);
                return;
            }
            var decision = MembershipMagazineRouting.TryCreateExclusiveDecision(baseline) ??
                aiCandidate ?? baseline ?? await classifier.ClassifyAsync(message, cancellationToken);
            var to = decision.Recipients.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var cc = decision.CcRecipients.Except(to, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (decision.Route != MailRoute.Forward || IsLoop(message) ||
                RoutingDecisionService.ContainsNoForwardInstruction(message.Subject + " " + message.Body) ||
                to.Length == 0 ||
                to.Concat(cc).Any(address => !ValidRecipient(address)))
            {
                await SaveAsync(state with
                {
                    Stage = ProcessingStage.ManualReview,
                    Reason = IsLoop(message)
                        ? IsInternalSender(message) ? "InternalSender" : "LoopOrAutomaticMessage"
                        : decision.Reason
                }, cancellationToken);
                return;
            }
            if (!(await store.GetControlsAsync(cancellationToken)).Send)
            {
                logger.LogWarning("Processing send gate closed. Reference={Reference}.", Reference(message.Id));
                return;
            }
            var destination = moveEnabled ? await mailbox.FindDestinationAsync(
                decision.TargetFolder ?? "Videresendt via regler", cancellationToken) : null;
            // Compare-and-swap claims the irreversible action before any network write.
            var claim = await SaveAsync(state with
            {
                Stage = ProcessingStage.Sending, To = to, Cc = cc,
                Reason = decision.Reason switch
                {
                    "AiRecipientCandidate" => "AiCandidateForward",
                    "MembershipMagazineExclusive" => "MembershipMagazineExclusive",
                    _ => "ForwardAttempt"
                },
                Destination = destination
            }, cancellationToken);
            if (claim is null)
            {
                return;
            }
            state = claim;
            try
            {
                // Recheck immediately before the external effect. A kill switch cannot recall an in-flight request.
                if (!(await store.GetControlsAsync(cancellationToken)).Send)
                {
                    await SaveAsync(state with { Stage = ProcessingStage.Pending, Reason = "SendGateClosed" }, cancellationToken);
                    return;
                }
                await mailbox.ForwardAsync(message.Id, to, cc, cancellationToken);
                var accepted = await SaveAsync(state with { Stage = ProcessingStage.Accepted, Reason = "GraphAccepted202" }, cancellationToken);
                if (accepted is null)
                {
                    logger.LogError("Processing state conflict after send Reference={Reference}; manual reconciliation required.",
                        Reference(message.Id));
                    return;
                }
                state = accepted;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or Azure.RequestFailedException)
            {
                logger.LogError("Processing send outcome uncertain Reference={Reference} ErrorType={Type}. Sending state blocks retry; manual reconciliation required.",
                    Reference(message.Id), error.GetType().Name);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return;
            }
        }
        if (state.Stage == ProcessingStage.Accepted && moveEnabled &&
            (await store.GetControlsAsync(cancellationToken)).Move)
        {
            var destination = state.Destination ?? await mailbox.FindDestinationAsync("Videresendt via regler", cancellationToken);
            var moving = await SaveAsync(state with { Stage = ProcessingStage.Moving, Destination = destination }, cancellationToken);
            if (moving is null)
            {
                return;
            }
            if (!(await store.GetControlsAsync(cancellationToken)).Move)
            {
                await SaveAsync(moving with { Stage = ProcessingStage.Accepted, Reason = "MoveGateClosed" }, cancellationToken);
                return;
            }
            try
            {
                await mailbox.MoveAsync(message.Id, destination, cancellationToken);
                await SaveAsync(moving with { Stage = ProcessingStage.Completed, Reason = "MoveConfirmed" }, cancellationToken);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or Azure.RequestFailedException)
            {
                logger.LogError("Processing move outcome uncertain Reference={Reference} ErrorType={Type}. Never resend; reconcile manually.",
                    Reference(message.Id), error.GetType().Name);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }
    }

    private async Task MoveOnlyAsync(EmailMessage message, ProcessingRecord state, bool moveEnabled,
        CancellationToken cancellationToken)
    {
        if (!moveEnabled || !(await store.GetControlsAsync(cancellationToken)).Move)
        {
            await SaveAsync(state with { Stage = ProcessingStage.ManualReview, Reason = "AbsenceMoveGateClosed" },
                cancellationToken);
            return;
        }
        var destination = await mailbox.FindDestinationAsync(AbsenceRouting.DestinationFolder, cancellationToken);
        var moving = await SaveAsync(state with
        {
            Stage = ProcessingStage.Moving,
            To = Array.Empty<string>(),
            Cc = Array.Empty<string>(),
            Reason = "AbsenceMoveOnly",
            Destination = destination
        }, cancellationToken);
        if (moving is null)
            return;
        if (!(await store.GetControlsAsync(cancellationToken)).Move)
        {
            await SaveAsync(moving with { Stage = ProcessingStage.ManualReview, Reason = "AbsenceMoveGateClosed" },
                cancellationToken);
            return;
        }
        try
        {
            await mailbox.MoveAsync(message.Id, destination, cancellationToken);
            await SaveAsync(moving with { Stage = ProcessingStage.Completed, Reason = "AbsenceMoveOnlyConfirmed" },
                cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or Azure.RequestFailedException)
        {
            logger.LogError("Absence move outcome uncertain Reference={Reference} ErrorType={Type}. No forwarding was attempted; reconcile manually.",
                Reference(message.Id), error.GetType().Name);
            if (cancellationToken.IsCancellationRequested)
                throw;
        }
    }

    private async Task<ProcessingRecord?> SaveAsync(ProcessingRecord record, CancellationToken cancellationToken)
    {
        var saved = await store.TrySaveAsync(record, record.Version, cancellationToken);
        if (saved is not null)
        {
            logger.LogInformation("Processing state Reference={Reference} Stage={Stage} Reason={Reason}",
                Reference(record.MessageId), record.Stage, record.Reason);
        }
        else
        {
            logger.LogWarning("Processing state conflict Reference={Reference}. No further action taken by this attempt.",
                Reference(record.MessageId));
        }
        return saved;
    }

    internal static bool IsLoop(EmailMessage message) =>
        message.IsAutomatic || IsInternalSender(message) ||
        message.Subject.Contains("[InfoMail-Forward:", StringComparison.OrdinalIgnoreCase) ||
        message.Body.Contains("[InfoMail-Forward:", StringComparison.OrdinalIgnoreCase);

    internal static bool IsInternalSender(EmailMessage message) =>
        MailAddress.TryCreate(message.From, out var sender) &&
        sender.Host.Equals("blind.dk", StringComparison.OrdinalIgnoreCase);

    private static bool ValidRecipient(string address) => MailAddress.TryCreate(address, out var parsed) &&
        parsed.Address == address && !address.Equals("info@blind.dk", StringComparison.OrdinalIgnoreCase);

    public static string Reference(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}

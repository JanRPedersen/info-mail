using InfoMail.Models;
using InfoMail.Services;

namespace InfoMail;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IInboxSource _inbox;
    private readonly IMailClassifier _classifier;
    private readonly IConfiguration _configuration;
    private readonly IForwardingLogStore _forwardingLogStore;

    public Worker(ILogger<Worker> logger, IInboxSource inbox, IMailClassifier classifier, IConfiguration configuration, IForwardingLogStore forwardingLogStore)
    {
        _logger = logger;
        _inbox = inbox;
        _classifier = classifier;
        _configuration = configuration;
        _forwardingLogStore = forwardingLogStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var mailboxAddress = _configuration["MailRobot:MailboxAddress"] ?? "info@blind.dk";
            _logger.LogInformation("Checking inbox for {MailboxAddress} at: {time}", mailboxAddress, DateTimeOffset.Now);

            var messages = await _inbox.GetMessagesAsync(stoppingToken);
            foreach (var message in messages)
            {
                var decision = await _classifier.ClassifyAsync(message, stoppingToken);
                var routeText = decision.Route == MailRoute.Forward ? "forward" : "manual review";

                _logger.LogInformation(
                    "Message {Id} from {From} classified as {Route}. Reason: {Reason}. Confidence: {Confidence:F2}",
                    message.Id,
                    message.From,
                    routeText,
                    decision.Reason,
                    decision.Confidence);

                if (decision.Route == MailRoute.Forward)
                {
                    var recipient = decision.Recipient ?? _configuration["MailRobot:ForwardRecipient"] ?? mailboxAddress;
                    _logger.LogInformation("Would forward message {Id} to {Recipient}", message.Id, recipient);

                    await _forwardingLogStore.LogForwardAsync(new ForwardingLogEntry
                    {
                        MessageId = message.Id,
                        Subject = message.Subject,
                        From = message.From,
                        To = recipient,
                        RuleName = decision.Reason,
                        Reason = decision.Reason,
                        ForwardedAt = DateTimeOffset.UtcNow,
                        TargetFolder = decision.TargetFolder ?? _configuration["MailRobot:DefaultTargetFolder"]
                    }, stoppingToken);
                }

                await _inbox.MarkAsProcessedAsync(message.Id, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(_configuration.GetValue("MailRobot:PollingIntervalSeconds", 30)), stoppingToken);
        }
    }
}

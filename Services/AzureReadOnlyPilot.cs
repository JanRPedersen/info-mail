using System.Globalization;
using Azure.Identity;

namespace InfoMail.Services;

public sealed class AzureReadOnlyPilot(
    IConfiguration configuration,
    GraphReadOnlyProbe probe,
    ILogger<AzureReadOnlyPilot> logger,
    DailySharePointClassifier? sharePointRules = null,
    MailProcessingEngine? processingEngine = null,
    SharePointShadowLog? shadowLog = null,
    RoutingFeedbackReport? feedbackReport = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = configuration.GetValue("AzurePilot:ReadingEnabled", false);
        var startTime = DateTimeOffset.MinValue;
        if (enabled && !DateTimeOffset.TryParseExact(configuration["AzurePilot:StartTimeUtc"], "O",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out startTime))
        {
            throw new InvalidOperationException("An explicit ISO 8601 AzurePilot:StartTimeUtc is required before enabling reads.");
        }

        logger.LogInformation("Azure read-only sampler started. ReadingEnabled={Enabled}. Sampler never sends or moves. Processing engine gates are logged separately. DocumentReferences=false.",
            enabled);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (sharePointRules is not null)
            {
                try
                {
                    await sharePointRules.RefreshAsync(stoppingToken);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or
                    Azure.RequestFailedException or AuthenticationFailedException or System.Xml.XmlException or
                    System.Text.Json.JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException ||
                    error is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("SharePointRules refresh failed. ErrorType={ErrorType} HTTP={Status} Validation={Validation}. Last validated rules retained; without a validated version all messages require manual review. Retry in five minutes.",
                        error.GetType().Name, error is HttpRequestException httpError ? (int?)httpError.StatusCode : null,
                        error is InvalidDataException ? error.Message : "Unavailable");
                }
            }

            if (processingEngine is not null)
            {
                try
                {
                    await processingEngine.RunAsync(stoppingToken);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidDataException or
                    Azure.RequestFailedException or AuthenticationFailedException or
                    System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or
                    System.Text.RegularExpressions.RegexMatchTimeoutException ||
                    error is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("Processing engine scan failed. ErrorType={Type}. No blind retries of writes; inspect persistent states before resolving uncertain actions.",
                        error.GetType().Name);
                }
            }

            if (shadowLog is not null)
            {
                await shadowLog.FlushAsync(stoppingToken);
            }
            if (feedbackReport is not null)
            {
                await feedbackReport.RunAsync(false, stoppingToken);
            }

            if (!enabled)
            {
                logger.LogInformation("Pilot heartbeat: waiting for scoped Exchange authorization and explicit read activation. No mailbox request made.");
            }
            else
            {
                try
                {
                    using var output = new StringWriter(CultureInfo.InvariantCulture);
                    await probe.RunSummaryAsync(output, startTime, stoppingToken, includeRoutingDetails: true);
                    foreach (var line in output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                    {
                        logger.LogInformation("Pilot sample: {Summary}", line);
                    }
                }
                catch (HttpRequestException error)
                {
                    logger.LogError("Pilot read failed. HTTP={Status}. No response body logged; retry in five minutes.", (int?)error.StatusCode);
                }
                catch (AuthenticationFailedException)
                {
                    logger.LogError("Managed identity authentication failed; retry in five minutes. No credentials logged.");
                }
                catch (InvalidDataException)
                {
                    logger.LogError("Graph response validation failed; no mail content logged. Retry in five minutes.");
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    logger.LogError("Catalog matching timed out; sample incomplete. No mail content logged. Retry in five minutes.");
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError("Pilot read timed out; retry in five minutes.");
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

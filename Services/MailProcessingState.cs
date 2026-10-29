using InfoMail.Models;

namespace InfoMail.Services;

public enum ProcessingStage
{
    Pending,
    ManualReview,
    Sending,
    Accepted,
    Moving,
    Completed,
    NeedsReview
}

public sealed record ProcessingRecord(
    string MessageId, ProcessingStage Stage, string Version,
    string[] To, string[] Cc, string Reason, string? Destination = null);

public sealed record ProcessingControls(bool Send, bool Move);

public interface IMailProcessingStore
{
    Task<ProcessingRecord> GetOrCreateAsync(string messageId, CancellationToken cancellationToken);
    Task<ProcessingRecord?> TrySaveAsync(ProcessingRecord record, string expectedVersion, CancellationToken cancellationToken);
    Task<ProcessingControls> GetControlsAsync(CancellationToken cancellationToken);
}

public interface IProcessingMailbox
{
    IAsyncEnumerable<EmailMessage> ReadInboxAsync(DateTimeOffset start, CancellationToken cancellationToken);
    Task ForwardAsync(string id, string[] to, string[] cc, CancellationToken cancellationToken);
    Task<string> FindDestinationAsync(CancellationToken cancellationToken) =>
        FindDestinationAsync("Videresendt via regler", cancellationToken);
    Task<string> FindDestinationAsync(string folderName, CancellationToken cancellationToken);
    Task MoveAsync(string id, string destination, CancellationToken cancellationToken);
}

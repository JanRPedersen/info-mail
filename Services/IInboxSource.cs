using InfoMail.Models;

namespace InfoMail.Services;

public interface IInboxSource
{
    Task<IReadOnlyList<EmailMessage>> GetMessagesAsync(CancellationToken cancellationToken);
    Task MarkAsProcessedAsync(string id, CancellationToken cancellationToken);
}

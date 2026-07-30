using InfoMail.Models;

namespace InfoMail.Services;

public interface IForwardingLogStore
{
    Task LogForwardAsync(ForwardingLogEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<ForwardingLogEntry>> GetLogAsync(CancellationToken cancellationToken);
}

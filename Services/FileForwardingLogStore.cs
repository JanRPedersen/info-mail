using System.Text.Json;
using InfoMail.Models;

namespace InfoMail.Services;

public class FileForwardingLogStore : IForwardingLogStore
{
    private readonly string _path;
    private readonly ILogger<FileForwardingLogStore> _logger;

    public FileForwardingLogStore(IConfiguration configuration, ILogger<FileForwardingLogStore> logger)
    {
        _path = configuration["MailRobot:ForwardingLogPath"] ?? Path.Combine(AppContext.BaseDirectory, "Data", "forwarded-log.json");
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public Task LogForwardAsync(ForwardingLogEntry entry, CancellationToken cancellationToken)
    {
        var entries = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<ForwardingLogEntry>>(File.ReadAllText(_path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ForwardingLogEntry>()
            : new List<ForwardingLogEntry>();

        entries.Add(entry);
        File.WriteAllText(_path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        _logger.LogInformation("Logged forwarded mail {MessageId} to {To}", entry.MessageId, entry.To);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ForwardingLogEntry>> GetLogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Task.FromResult<IReadOnlyList<ForwardingLogEntry>>(Array.Empty<ForwardingLogEntry>());
        }

        var entries = JsonSerializer.Deserialize<List<ForwardingLogEntry>>(File.ReadAllText(_path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<ForwardingLogEntry>();
        return Task.FromResult<IReadOnlyList<ForwardingLogEntry>>(entries);
    }
}

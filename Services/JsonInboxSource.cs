using System.Text.Json;
using InfoMail.Models;

namespace InfoMail.Services;

public class JsonInboxSource : IInboxSource
{
    private readonly string _path;
    private readonly ILogger<JsonInboxSource> _logger;
    private readonly HashSet<string> _processedIds = new(StringComparer.Ordinal);

    public JsonInboxSource(IConfiguration configuration, ILogger<JsonInboxSource> logger)
    {
        _path = configuration["MailRobot:InboxDataPath"] ?? "Data/inbox-samples.json";
        _logger = logger;
    }

    public Task<IReadOnlyList<EmailMessage>> GetMessagesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            _logger.LogWarning("Inbox file not found: {Path}", _path);
            return Task.FromResult<IReadOnlyList<EmailMessage>>(Array.Empty<EmailMessage>());
        }

        var json = File.ReadAllText(_path);
        var messages = JsonSerializer.Deserialize<List<EmailMessage>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<EmailMessage>();

        return Task.FromResult<IReadOnlyList<EmailMessage>>(messages.Where(message => !_processedIds.Contains(message.Id)).ToList());
    }

    public Task MarkAsProcessedAsync(string id, CancellationToken cancellationToken)
    {
        _processedIds.Add(id);
        _logger.LogInformation("Marked message {Id} as processed", id);
        return Task.CompletedTask;
    }
}

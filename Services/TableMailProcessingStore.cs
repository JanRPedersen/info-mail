using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace InfoMail.Services;

public sealed class TableMailProcessingStore(TableClient table) : IMailProcessingStore
{
    public async Task<ProcessingRecord> GetOrCreateAsync(string messageId, CancellationToken cancellationToken)
    {
        var key = Key(messageId);
        var existing = await table.GetEntityIfExistsAsync<TableEntity>("Messages", key, cancellationToken: cancellationToken);
        if (existing.HasValue)
        {
            var restored = Read(existing.Value);
            if (restored.MessageId != messageId)
            {
                throw new InvalidDataException("Processing state message ID mismatch.");
            }
            return restored;
        }
        var entity = new TableEntity("Messages", key)
        {
            ["MessageId"] = messageId,
            ["Stage"] = ProcessingStage.Pending.ToString(),
            ["To"] = "[]", ["Cc"] = "[]", ["Reason"] = "New"
        };
        try
        {
            await table.AddEntityAsync(entity, cancellationToken);
        }
        catch (RequestFailedException error) when (error.Status == 409)
        {
            // Another replica may have claimed the same immutable message.
        }
        var result = Read((await table.GetEntityAsync<TableEntity>("Messages", key, cancellationToken: cancellationToken)).Value);
        if (result.MessageId != messageId)
        {
            throw new InvalidDataException("Processing state message ID mismatch.");
        }
        return result;
    }

    public async Task<ProcessingRecord?> TrySaveAsync(ProcessingRecord record, string expectedVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion) || expectedVersion == "*")
        {
            throw new InvalidDataException("A specific processing state ETag is required.");
        }
        var entity = new TableEntity("Messages", Key(record.MessageId))
        {
            ["MessageId"] = record.MessageId,
            ["Stage"] = record.Stage.ToString(),
            ["To"] = JsonSerializer.Serialize(record.To),
            ["Cc"] = JsonSerializer.Serialize(record.Cc),
            ["Reason"] = record.Reason,
            ["Destination"] = record.Destination ?? "",
            ["UpdatedUtc"] = DateTimeOffset.UtcNow
        };
        try
        {
            var response = await table.UpdateEntityAsync(entity, new ETag(expectedVersion), TableUpdateMode.Replace, cancellationToken);
            if (!response.Headers.TryGetValue("ETag", out var etag) || string.IsNullOrWhiteSpace(etag))
            {
                throw new InvalidDataException("State update did not return an ETag.");
            }
            return record with { Version = etag };
        }
        catch (RequestFailedException error) when (error.Status == 412)
        {
            return null;
        }
    }

    public async Task<ProcessingControls> GetControlsAsync(CancellationToken cancellationToken)
    {
        var control = await table.GetEntityIfExistsAsync<TableEntity>("Control", "Forwarding", cancellationToken: cancellationToken);
        var entity = control.HasValue ? control.Value : null;
        return new(entity?.GetBoolean("SendEnabled") == true, entity?.GetBoolean("MoveEnabled") == true);
    }

    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ProcessingRecord Read(TableEntity? entity)
    {
        if (entity is null || !Enum.TryParse<ProcessingStage>(entity.GetString("Stage"), out var stage) ||
            !Enum.IsDefined(stage) || string.IsNullOrWhiteSpace(entity.GetString("MessageId")) ||
            string.IsNullOrWhiteSpace(entity.ETag.ToString()) || entity.ETag.ToString() == "*")
        {
            throw new InvalidDataException("Invalid processing state.");
        }
        return new(entity.GetString("MessageId")!, stage, entity.ETag.ToString(),
            JsonSerializer.Deserialize<string[]>(entity.GetString("To") ?? throw new InvalidDataException("Missing recipient state."))
                ?? throw new InvalidDataException("Invalid recipient state."),
            JsonSerializer.Deserialize<string[]>(entity.GetString("Cc") ?? throw new InvalidDataException("Missing cc state."))
                ?? throw new InvalidDataException("Invalid cc state."),
            entity.GetString("Reason") ?? "", entity.GetString("Destination"));
    }
}

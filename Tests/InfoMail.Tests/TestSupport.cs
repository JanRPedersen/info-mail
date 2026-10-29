using System.Text.Json;
using System.Text.Json.Serialization;
using InfoMail.Services;
using Microsoft.Extensions.Configuration;

namespace InfoMail.Tests;

internal static class TestConfiguration
{
    public static IConfiguration Create(object mailRobot)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { MailRobot = mailRobot },
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }
}

internal sealed class StubDocumentReferenceService : IDocumentReferenceService
{
    private readonly IReadOnlyList<string> _texts;

    public StubDocumentReferenceService(params string[] texts) => _texts = texts;

    public Task<IReadOnlyList<string>> GetReferenceTextsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_texts);
}

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "InfoMail.Tests", Guid.NewGuid().ToString("N"));

    public TestDirectory() => Directory.CreateDirectory(Path);

    public string FilePath(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

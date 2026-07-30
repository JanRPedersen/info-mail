using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoMail.Services;

public class FolderDocumentReferenceService : IDocumentReferenceService
{
    private readonly string _folder;
    private readonly object _lock = new();
    private IReadOnlyList<string> _cachedTexts = Array.Empty<string>();

    public FolderDocumentReferenceService(IConfiguration configuration)
    {
        _folder = ResolveFolder(configuration);
        Directory.CreateDirectory(_folder);
        _cachedTexts = LoadReferenceTexts();
        StartWatching();
    }

    public Task<IReadOnlyList<string>> GetReferenceTextsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_cachedTexts);
        }
    }

    private static string ResolveFolder(IConfiguration configuration)
    {
        var configuredFolder = configuration["MailRobot:DocumentReferenceFolder"];
        if (string.IsNullOrWhiteSpace(configuredFolder))
        {
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "Documents"));
        }

        return Path.IsPathRooted(configuredFolder)
            ? configuredFolder
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configuredFolder));
    }

    private void StartWatching()
    {
        if (!Directory.Exists(_folder))
        {
            return;
        }

        var watcher = new FileSystemWatcher(_folder)
        {
            Filter = "*.docx",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Changed += (_, _) => ReloadFromDisk();
        watcher.Created += (_, _) => ReloadFromDisk();
        watcher.Deleted += (_, _) => ReloadFromDisk();
        watcher.Renamed += (_, _) => ReloadFromDisk();
        watcher.Error += (_, args) => Console.WriteLine($"Document watcher error: {args.GetException()?.Message}");
    }

    private void ReloadFromDisk()
    {
        lock (_lock)
        {
            _cachedTexts = LoadReferenceTexts();
        }
    }

    private IReadOnlyList<string> LoadReferenceTexts()
    {
        if (!Directory.Exists(_folder))
        {
            return Array.Empty<string>();
        }

        var texts = new List<string>();
        foreach (var file in Directory.GetFiles(_folder, "*.docx", SearchOption.AllDirectories).OrderBy(f => f))
        {
            try
            {
                using var archive = ZipFile.OpenRead(file);
                var part = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("document.xml", StringComparison.OrdinalIgnoreCase));
                if (part is null)
                {
                    continue;
                }

                using var stream = part.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var xml = reader.ReadToEnd();
                var plainText = StripXmlTags(xml);
                texts.Add(plainText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read document {file}: {ex.Message}");
            }
        }

        return texts;
    }

    private static string StripXmlTags(string xml)
    {
        var withoutTags = Regex.Replace(xml, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return decoded.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
    }
}

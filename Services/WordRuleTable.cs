using System.IO.Compression;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace InfoMail.Services;

public sealed record WordRuleRow(string Subject, string[] Addresses);
public sealed record WordRuleSnapshot(string Hash, IReadOnlyList<WordRuleRow> Rows);

public static class WordRuleTable
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static WordRuleSnapshot Read(Stream docx)
    {
        using var zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true);
        var part = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Word document part missing.");
        if (part.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Word document exceeds the XML size limit.");
        }
        using var stream = part.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var tables = document.Descendants(W + "tbl").Where(table =>
        {
            var header = table.Elements(W + "tr").FirstOrDefault()?.Elements(W + "tc").Select(CellText).ToArray();
            return header is { Length: 2 } && Normalize(header[0]) == "emne" && Normalize(header[1]) == "mailadresse";
        }).ToArray();
        if (tables.Length != 1)
        {
            throw new InvalidDataException("Exactly one Emne/Mailadresse table is required.");
        }
        if (tables[0].Descendants(W + "ins").Any() || tables[0].Descendants(W + "del").Any())
        {
            throw new InvalidDataException("Accept or reject tracked changes in the rule table before publishing rules.");
        }
        var result = new List<WordRuleRow>();
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in tables[0].Elements(W + "tr").Skip(1))
        {
            var cells = row.Elements(W + "tc").ToArray();
            if (cells.Length != 2 || row.Descendants(W + "vMerge").Any() ||
                row.Descendants(W + "gridSpan").Any() || row.Descendants(W + "tbl").Any())
            {
                throw new InvalidDataException("Rule table must have two unmerged cells per row.");
            }
            var subject = CellText(cells[0]);
            var addressText = CellText(cells[1]);
            if (subject.Length == 0 && addressText.Length == 0)
            {
                continue;
            }
            if (subject.Length == 0 || subject.Length > 500 || !subjects.Add(Normalize(subject)))
            {
                throw new InvalidDataException("Empty, duplicate or excessively long table subject.");
            }
            var addresses = new List<string>();
            foreach (var token in Regex.Split(addressText, @"\s+og\s+|[;,\r\n]+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var address = token.Trim();
                if (address.Length == 0)
                {
                    continue;
                }
                if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address ||
                    address.Equals("info@blind.dk", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Table recipient must be an explicit email address and cannot be the inbox itself.");
                }
                addresses.Add(address.ToLowerInvariant());
            }
            if (addresses.Count == 0)
            {
                throw new InvalidDataException("Each rule row must have at least one email address.");
            }
            result.Add(new WordRuleRow(subject, addresses.Distinct().Order().ToArray()));
        }
        if (result.Count is 0 or > 250)
        {
            throw new InvalidDataException("Rule table must contain between 1 and 250 rows.");
        }
        var canonical = System.Text.Json.JsonSerializer.Serialize(result.OrderBy(row => Normalize(row.Subject)));
        return new WordRuleSnapshot(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), result);
    }

    public static string Apply(string baselineJson, WordRuleSnapshot snapshot)
    {
        var root = JsonNode.Parse(baselineJson) ?? throw new InvalidDataException("Baseline catalog missing.");
        var topics = root["Topics"]!.AsArray();
        var matchers = root["TopicMatchers"]!.AsObject();
        var aliases = root["SharePointSubjects"]?.AsObject();
        var remaining = snapshot.Rows.ToDictionary(row => Normalize(row.Subject));
        foreach (var topic in topics)
        {
            var id = topic!["Id"]!.GetValue<string>();
            var candidates = new[] { topic["Topic"]!.GetValue<string>() }
                .Concat(aliases?[id]?.AsArray().Select(node => node!.GetValue<string>()) ?? Array.Empty<string>());
            var found = candidates.Select(Normalize).Where(remaining.ContainsKey).Distinct().ToArray();
            var row = found.Length > 0 ? new WordRuleRow(topic["Topic"]!.GetValue<string>(),
                found.SelectMany(key => remaining[key].Addresses).Distinct().Order().ToArray()) : null;
            topic["To"] = new JsonArray((row?.Addresses ?? Array.Empty<string>()).Select(address => JsonValue.Create(address)).ToArray());
            topic["Cc"] = new JsonArray();
            topic["Unresolved"] = row is null ? new JsonArray("SubjectMissingFromSharePoint") : new JsonArray();
            if (row is not null)
            {
                foreach (var key in found)
                {
                    remaining.Remove(key);
                }
            }
        }
        foreach (var row in remaining.Values)
        {
            var id = "sharepoint-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(row.Subject))))[..16];
            topics.Add(new JsonObject
            {
                ["Id"] = id, ["Topic"] = row.Subject,
                ["To"] = new JsonArray(row.Addresses.Select(address => JsonValue.Create(address)).ToArray()),
                ["Unresolved"] = new JsonArray("NewSubjectNeedsMatchReview")
            });
            matchers[id] = new JsonArray(row.Subject);
        }
        // The Word table is the sole recipient source; orphan keyword rules must not retain stale addresses.
        root["KeywordRules"] = new JsonArray();
        var json = root.ToJsonString();
        _ = CatalogMailClassifier.FromJson(json);
        return json;
    }

    private static string CellText(XElement cell) =>
        string.Join("\n", cell.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))).Trim();

    private static string Normalize(string value) =>
        Regex.Replace(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('–', '-').Replace('—', '-'),
            @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
}

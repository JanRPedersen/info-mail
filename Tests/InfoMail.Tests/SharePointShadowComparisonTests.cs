using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class SharePointShadowComparisonTests
{
    private static ShadowLogEntry Entry() => new(new string('A', 64), DateTimeOffset.Parse("2026-10-22T13:00:00Z"),
        "Real subject", "Completed", "gpt-5.4-mini", "version", "Forward", new[] { "donors" },
        "ManualReview", new[] { "donors" }, new[] { "team@blind.dk" }, Array.Empty<string>(), null,
        false, true, new[] { "document | hash" }, false, "Kort kategoribaseret begrundelse.");

    [Fact]
    public async Task CreatesDedicatedListAndVerifiesUniqueKeyAndHumanFields()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var url = await new SharePointShadowComparison(http, new Credential()).EnsureAsync(CancellationToken.None);
        Assert.Equal("https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AIMailSammenligning", url);
        Assert.Equal(1, handler.ListCreates);
        Assert.Contains(handler.Columns!, field => field?["name"]!.GetValue<string>() == "ActualTo");
        Assert.Contains(handler.Columns!, field => field?["name"]!.GetValue<string>() == "HumanVerdict");
        Assert.Contains(handler.Columns!, field => field?["name"]!.GetValue<string>() == "ActualActor");
        var reference = Assert.IsType<JsonObject>(handler.Columns!.Single(field => field?["name"]!.GetValue<string>() == "ShadowReference"));
        Assert.True(reference["indexed"]!.GetValue<bool>());
        Assert.True(reference["enforceUniqueValues"]!.GetValue<bool>());
        Assert.Contains("udfoerte handlinger", handler.Description);
    }

    [Fact]
    public async Task RepeatedExportUpdatesOnlyMachineFieldsAndPreservesHumanFeedback()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var service = new SharePointShadowComparison(http, new Credential());
        await service.UpsertAsync(Entry(), CancellationToken.None);
        handler.Item!["ActualTo"] = "human-choice@blind.dk";
        handler.Item["ActualAction"] = "Videresendt";
        handler.Item["HumanVerdict"] = "Uenig med AI";
        handler.Item["HumanComment"] = "Medarbejderens vurdering";
        await service.UpsertAsync(Entry() with { Explanation = "Opdateret forklaring" }, CancellationToken.None);
        Assert.Equal(1, handler.ItemCreates);
        Assert.Equal(1, handler.Patches);
        Assert.Equal("human-choice@blind.dk", handler.Item["ActualTo"]!.GetValue<string>());
        Assert.Equal("Uenig med AI", handler.Item["HumanVerdict"]!.GetValue<string>());
        Assert.Equal("Medarbejderens vurdering", handler.Item["HumanComment"]!.GetValue<string>());
        Assert.Equal("Opdateret forklaring", handler.Item["AiSummary"]!.GetValue<string>());
        Assert.DoesNotContain("ActualAction", handler.LastPatch!);
        Assert.DoesNotContain("HumanVerdict", handler.LastPatch!);
    }

    [Fact]
    public async Task UncertainCreationReconcilesUniqueReferenceInsteadOfDuplicatingRow()
    {
        var handler = new Handler { CommitThenConflict = true };
        using var http = new HttpClient(handler);
        await new SharePointShadowComparison(http, new Credential()).UpsertAsync(Entry(), CancellationToken.None);
        Assert.Equal(1, handler.ItemCreates);
        Assert.Equal(1, handler.Patches);
    }

    [Fact]
    public async Task IncompatibleExistingListIsNotSilentlyModified()
    {
        var handler = new Handler { BadSchema = true };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SharePointShadowComparison(http, new Credential()).EnsureAsync(CancellationToken.None));
        Assert.Equal(0, handler.ListCreates);
        Assert.Equal(0, handler.ItemCreates);
    }

    [Fact]
    public void MachineFieldsDoNotInventHumanOutcomeAndSyntheticDataIsExplicit()
    {
        var fields = SharePointShadowComparison.MachineFields(Entry() with { IsSynthetic = true });
        Assert.True((bool)fields["IsSynthetic"]);
        Assert.Equal("Foreslaa manuel vurdering", fields["AiSuggestion"]);
        Assert.Equal("Foreslaa videresendelse", fields["RuleSuggestion"]);
        Assert.DoesNotContain("ActualAction", fields.Keys);
        Assert.DoesNotContain("ActualTo", fields.Keys);
        Assert.DoesNotContain("HumanVerdict", fields.Keys);
        Assert.DoesNotContain("Body", fields.Keys);
        Assert.DoesNotContain("From", fields.Keys);
    }

    [Fact]
    public async Task ExplicitLegacyConfirmationSetsOnlyActorWithExpectedEtag()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var service = new SharePointShadowComparison(http, new Credential());
        await service.UpsertAsync(Entry(), CancellationToken.None);
        handler.Item!["ActualAction"] = "Videresendt";
        handler.Item["ActualTo"] = "human@blind.dk";
        handler.Item["HumanComment"] = "Keep employee comment";
        Assert.Equal(1, await service.ConfirmExistingEmployeeFeedbackAsync(CancellationToken.None));
        Assert.Equal("\"feedback-version\"", handler.IfMatch);
        using var body = JsonDocument.Parse(handler.LastPatch!);
        Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("Medarbejder", body.RootElement.GetProperty("ActualActor").GetString());
        Assert.Equal("human@blind.dk", handler.Item["ActualTo"]!.GetValue<string>());
        Assert.Equal("Keep employee comment", handler.Item["HumanComment"]!.GetValue<string>());
        Assert.Equal(0, await service.ConfirmExistingEmployeeFeedbackAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true, "", "Videresendt", false)]
    [InlineData(false, "Regelrobot", "Videresendt", false)]
    [InlineData(false, "Medarbejder", "Videresendt", false)]
    [InlineData(false, "", "", false)]
    [InlineData(false, "", "Ukendt", false)]
    [InlineData(false, "", "Videresendt", true)]
    public void AttributionNeverOverwritesKnownActorOrLabelsSyntheticData(bool synthetic, string actor, string action, bool expected)
    {
        var row = JsonSerializer.SerializeToElement(new
        {
            id = "1", fields = new { IsSynthetic = synthetic, ActualActor = actor, ActualAction = action }
        });
        Assert.Equal(expected, SharePointShadowComparison.NeedsEmployeeAttribution(row));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int ListCreates { get; private set; }
        public int ItemCreates { get; private set; }
        public int Patches { get; private set; }
        public JsonArray? Columns { get; private set; }
        public JsonObject? Item { get; private set; }
        public string? LastPatch { get; private set; }
        public string? Description { get; private set; }
        public string? IfMatch { get; private set; }
        public bool BadSchema { get; init; }
        public bool CommitThenConflict { get; init; }
        private bool _created;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
            Assert.Contains(SharePointRuleSource.SiteId, request.RequestUri.AbsolutePath);
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/lists") && request.Method == HttpMethod.Get)
            {
                return Json(new { value = _created || BadSchema ? new[] { new { id = "list-id",
                    displayName = SharePointShadowComparison.ListName,
                    webUrl = "https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AIMailSammenligning" } } : Array.Empty<object>() });
            }
            if (path.EndsWith("/lists") && request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Columns = body["columns"]!.AsArray();
                Description = body["description"]!.GetValue<string>();
                ListCreates++;
                _created = true;
                return Json(new { id = "list-id", webUrl = "https://blindesamfund.sharepoint.com/sites/Infomail/Lists/AIMailSammenligning" });
            }
            if (path.EndsWith("/columns"))
                return Json(new { value = BadSchema ? new JsonArray() : Columns });
            if (request.Method == HttpMethod.Get && path.EndsWith("/items"))
            {
                if (request.RequestUri.Query.Contains("filter"))
                {
                    Assert.Contains("ShadowReference", Uri.UnescapeDataString(request.RequestUri.Query));
                    Assert.Contains(new string('A', 64), Uri.UnescapeDataString(request.RequestUri.Query));
                }
                return Json(new { value = Item is null ? Array.Empty<object>() :
                    new[] { new { id = "item-id", eTag = "\"feedback-version\"", fields = Item } } });
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/items"))
            {
                ItemCreates++;
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Item = body["fields"]!.AsObject();
                return CommitThenConflict ? new(HttpStatusCode.Conflict) : Json(new { id = "item-id" });
            }
            if (request.Method == HttpMethod.Patch && path.EndsWith("/fields"))
            {
                Patches++;
                LastPatch = await request.Content!.ReadAsStringAsync(cancellationToken);
                IfMatch = request.Headers.TryGetValues("If-Match", out var tags) ? Assert.Single(tags) : null;
                foreach (var field in JsonNode.Parse(LastPatch)!.AsObject())
                    Item![field.Key] = field.Value?.DeepClone();
                return Json(new { });
            }
            throw new InvalidOperationException("Unexpected request.");
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value))
        };
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

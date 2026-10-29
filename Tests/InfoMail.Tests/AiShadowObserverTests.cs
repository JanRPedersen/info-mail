using System.Net;
using System.Text.Json;
using Azure.Core;
using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoMail.Tests;

public class AiShadowObserverTests
{
    private const string Catalog = """{"Topics":[{"Id":"donors"}],"KeywordRules":[],"EmployeeFeedbackExamples":[{"Subject":"donation","ActualAction":"Videresendt","ActualRecipients":["team@blind.dk"]}],"TargetFolder":"Reviewed"}""";

    [Fact]
    public async Task RequestUsesIdentityAndStructuredOutputWithoutBaselineOrMailMetadata()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var store = new Store();
        var credential = new Credential();
        var logger = new RecordingLogger();
        var baseline = Baseline();
        await Observer(http, store, credential, logger).ObserveAsync(Message(), baseline, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions", handler.Url);
        Assert.Equal("Bearer synthetic-token", handler.Authorization);
        Assert.Equal(new[] { "https://cognitiveservices.azure.com/.default" }, credential.Scopes);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var root = request.RootElement;
        Assert.Equal("gpt-5.4-mini", root.GetProperty("model").GetString());
        Assert.Equal(1024, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(root.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Contains(Catalog, messages[0].GetProperty("content").GetString());
        Assert.Contains("untrusted", messages[0].GetProperty("content").GetString());
        using var mail = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("PRIVATE-SUBJECT", mail.RootElement.GetProperty("Subject").GetString());
        Assert.Equal("PRIVATE-BODY", mail.RootElement.GetProperty("Body").GetString());
        Assert.Equal(2, mail.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("PRIVATE-ID", handler.Body);
        Assert.DoesNotContain("PRIVATE-FROM", handler.Body);
        Assert.DoesNotContain("PRIVATE-BASELINE", handler.Body);
        Assert.True(store.Result!.RouteAgrees);
        Assert.True(store.Result.RulesAgree);
        Assert.Equal(20, store.LastLimit);
        Assert.Equal(64, store.Reference!.Length);
        Assert.DoesNotContain("PRIVATE", string.Join("", logger.Messages));
        Assert.DoesNotContain("synthetic-token", string.Join("", logger.Messages));
        Assert.Equal(MailRoute.Forward, baseline.Route);
        Assert.Equal("PRIVATE-BASELINE", baseline.Reason);
    }

    [Fact]
    public async Task DisagreementIsRecordedWithoutChangingBaseline()
    {
        var handler = new Handler { Answer = """{"route":"ManualReview","ruleIds":[],"recipientCandidates":[]}""" };
        using var http = new HttpClient(handler);
        var store = new Store();
        var baseline = Baseline();
        await Observer(http, store).ObserveAsync(Message(), baseline, CancellationToken.None);
        Assert.False(store.Result!.RouteAgrees);
        Assert.False(store.Result.RulesAgree);
        Assert.Equal(MailRoute.Forward, baseline.Route);
        Assert.Equal(new[] { "donors" }, baseline.MatchedRules);
    }

    [Fact]
    public async Task SourceConflictForcesManualReviewEvenIfModelReturnsForward()
    {
        var handler = new Handler { Answer = """{"route":"Forward","ruleIds":["donors"],"recipientCandidates":[],"sourceConflict":true}""" };
        using var http = new HttpClient(handler);
        var store = new Store();
        var queue = new LogQueue();
        var observer = new AiShadowObserver(http, new Credential(), store, Config(), new RecordingLogger(),
            queue, new Knowledge());
        Assert.True(await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None));
        Assert.Equal("ManualReview", store.Result!.Route);
        Assert.True(store.Result.SourceConflict);
        var entry = queue.Entries.Last();
        Assert.True(entry.SourceConflict);
        Assert.Equal(new[] { "document.docx | version" }, entry.Sources);
        Assert.Contains("KnowledgeDocuments", handler.Body);
        Assert.Contains("all source text", handler.Body);
        using var request = JsonDocument.Parse(handler.Body!);
        Assert.Contains(request.RootElement.GetProperty("response_format").GetProperty("json_schema")
            .GetProperty("schema").GetProperty("required").EnumerateArray(),
            field => field.GetString() == "sourceConflict");
    }

    [Fact]
    public async Task FeedbackRecipientCanBeForwardCandidateEvenWhenModelRouteRequiresReview()
    {
        var handler = new Handler
        {
            Answer = """{"route":"ManualReview","ruleIds":[],"recipientCandidates":["team@blind.dk"],"sourceConflict":true}"""
        };
        using var http = new HttpClient(handler);
        var observer = new AiShadowObserver(http, new Credential(), new Store(), Config(),
            new RecordingLogger(), knowledge: new Knowledge());

        var evaluation = await observer.EvaluateAsync(Message(), Baseline(), CancellationToken.None);

        Assert.True(evaluation.Completed);
        Assert.Equal(MailRoute.Forward, evaluation.CandidateDecision!.Route);
        Assert.Equal(new[] { "team@blind.dk" }, evaluation.CandidateDecision.Recipients);
    }

    [Fact]
    public async Task SelectedActiveRuleSuppliesRecipientCandidateEvenWhenAiRouteIsManual()
    {
        var handler = new Handler
        {
            Answer = """{"route":"ManualReview","ruleIds":["donors"],"recipientCandidates":[]}"""
        };
        using var http = new HttpClient(handler);
        var store = new Store
        {
            CatalogOverride = """{"Topics":[{"Id":"donors","To":["donors@blind.dk"],"Unresolved":[]}],"KeywordRules":[]}"""
        };
        var observer = new AiShadowObserver(http, new Credential(), store, Config(),
            new RecordingLogger());

        var evaluation = await observer.EvaluateAsync(Message(), Baseline(), CancellationToken.None);

        Assert.Equal(new[] { "donors@blind.dk" }, evaluation.CandidateDecision!.Recipients);
        Assert.Equal(new[] { "donors@blind.dk" }, store.Result!.CandidateTo);
    }

    [Fact]
    public async Task IncompleteSourceSetLogsFailureWithoutCallingModelOrUsingPartialContext()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var queue = new LogQueue();
        var store = new Store();
        var observer = new AiShadowObserver(http, new Credential(), store, Config(), new RecordingLogger(),
            queue, new Knowledge { Fail = true });
        Assert.False(await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, store.ClaimCalls);
        Assert.Equal("SourcesUnavailableOrInvalid", Assert.Single(queue.Entries).Status);
    }

    [Fact]
    public async Task ManualListRestrictionCannotBeOverriddenByModelForwardSuggestion()
    {
        var handler = new Handler { Answer = """{"route":"Forward","ruleIds":["donors"],"recipientCandidates":[],"sourceConflict":false}""" };
        using var http = new HttpClient(handler);
        var store = new Store();
        var observer = new AiShadowObserver(http, new Credential(), store, Config(), new RecordingLogger(),
            knowledge: new Knowledge { Manual = true });
        Assert.True(await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None));
        Assert.Equal("ManualReview", store.Result!.Route);
    }

    private sealed class Knowledge : IShadowKnowledgeSource
    {
        public bool Fail { get; init; }
        public bool Manual { get; init; }
        public Task<ShadowRules> EnrichAsync(ShadowRules rules, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new HttpRequestException("PRIVATE source response", null, HttpStatusCode.Forbidden);
            var catalog = System.Text.Json.Nodes.JsonNode.Parse(rules.Catalog)!.AsObject();
            catalog["KnowledgeDocuments"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"Name":"document.docx","Content":"all source text"}]""");
            catalog["SourceReferences"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"Name":"document.docx","Version":"version"}]""");
            if (Manual)
                catalog["Topics"]![0]!["ManualAction"] = "Confidence threshold needs manual verification";
            return Task.FromResult(new ShadowRules(catalog.ToJsonString(), "combined-version"));
        }
    }

    [Fact]
    public async Task ApprovedDecisionSummaryIsStoredButNeverPrintedInRuntimeLogs()
    {
        var handler = new Handler
        {
            Answer = """{"route":"Forward","ruleIds":["donors"],"recipientCandidates":[],"explanation":"Donorkategori; regel donors. Ingen kildekonflikt fundet."}"""
        };
        using var http = new HttpClient(handler);
        var store = new Store();
        var logger = new RecordingLogger();
        var queue = new LogQueue();
        var observer = new AiShadowObserver(http, new Credential(), store,
            Config(("AiShadow:IncludeExplanation", "true")), logger, queue);
        Assert.True(await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None));
        Assert.Equal("Donorkategori; regel donors. Ingen kildekonflikt fundet.", store.Result!.Explanation);
        Assert.Equal(store.Result.Explanation, queue.Entries.Last().Explanation);
        Assert.DoesNotContain("Donorkategori", string.Join("", logger.Messages));
    }

    [Theory]
    [InlineData("")]
    [InlineData("line1\nline2")]
    public async Task InvalidDecisionSummaryIsNotPublishedAsSuccess(string summary)
    {
        var handler = new Handler { Answer = JsonSerializer.Serialize(new { route = "Forward", ruleIds = new[] { "donors" }, explanation = summary }) };
        using var http = new HttpClient(handler);
        var store = new Store();
        Assert.False(await Observer(http, store, configuration: Config(("AiShadow:IncludeExplanation", "true")))
            .ObserveAsync(Message(), Baseline(), CancellationToken.None));
        Assert.Null(store.Result);
        Assert.True(store.Failed);
    }

    [Fact]
    public async Task ExcessiveDecisionSummaryIsRejected()
    {
        var handler = new Handler { Answer = JsonSerializer.Serialize(new { route = "Forward", ruleIds = new[] { "donors" }, explanation = new string('x', 601) }) };
        using var http = new HttpClient(handler);
        var store = new Store();
        Assert.False(await Observer(http, store, configuration: Config(("AiShadow:IncludeExplanation", "true")))
            .ObserveAsync(Message(), Baseline(), CancellationToken.None));
        Assert.True(store.Failed);
    }

    private sealed class LogQueue : IShadowLogQueue
    {
        public List<ShadowLogEntry> Entries { get; } = new();
        public Task QueueAsync(ShadowLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AzureSummaryKeepsRuleDecisionEvenWhenShadowDisagreesOrFails(HttpStatusCode status)
    {
        var handler = new Handler
        {
            Status = status, Answer = """{"route":"ManualReview","ruleIds":[],"recipientCandidates":[]}"""
        };
        using var http = new HttpClient(handler);
        var store = new Store();
        var credential = new Credential();
        var probe = new GraphReadOnlyProbe(http, credential, new Classifier(), Observer(http, store, credential));
        using var output = new StringWriter();
        await probe.RunSummaryAsync(output, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("ForwardSuggestions=1 ManualReview=0", output.ToString());
        Assert.DoesNotContain("PRIVATE", output.ToString());
        if (status == HttpStatusCode.OK)
        {
            Assert.False(store.Result!.RouteAgrees);
        }
        else
        {
            Assert.True(store.Failed);
        }
    }

    [Fact]
    public async Task OneShotProbeDoesNotInvokeShadowEvenIfObserverIsPresent()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var store = new Store();
        var credential = new Credential();
        var probe = new GraphReadOnlyProbe(http, credential, new Classifier(), Observer(http, store, credential));
        using var output = new StringWriter();
        await probe.RunAsync(output, CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, store.ClaimCalls);
    }

    [Fact]
    public async Task ExistingClaimOrBudgetLimitMakesNoModelCall()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var store = new Store { ClaimAvailable = false };
        await Observer(http, store).ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.Equal(0, handler.Calls);
        Assert.Null(store.Result);
    }

    [Fact]
    public async Task MessagesBeforeShadowActivationAreNotSent()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var store = new Store();
        var message = Message();
        message.ReceivedAt = DateTimeOffset.Parse("2025-12-31T23:59:59Z");
        await Observer(http, store).ObserveAsync(message, Baseline(), CancellationToken.None);
        Assert.Equal(0, store.ClaimCalls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InputLimitIsExactAndOversizeIsSkippedRatherThanTruncated()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var store = new Store();
        var size = Catalog.Length + Message().Subject.Length + Message().Body.Length;
        await Observer(http, store, configuration: Config(("AiShadow:MaximumInputCharacters", size.ToString())))
            .ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        await Observer(http, store, configuration: Config(("AiShadow:MaximumInputCharacters", (size - 1).ToString())))
            .ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, store.ClaimCalls);
    }

    [Theory]
    [InlineData("""{"route":"Delete","ruleIds":[]}""")]
    [InlineData("""{"route":"Forward","ruleIds":["unknown"]}""")]
    [InlineData("""{"route":"Forward","ruleIds":["donors","donors"]}""")]
    [InlineData("""{"route":"Forward","ruleIds":[],"extra":"PRIVATE"}""")]
    [InlineData("""{"route":"Forward"}""")]
    [InlineData("""{"route":"Forward","ruleIds":null}""")]
    [InlineData("""{"route":"Forward","ruleIds":[],"recipientCandidates":["outsider@blind.dk"]}""")]
    [InlineData("not json PRIVATE")]
    public async Task InvalidAnswersFailExplicitlyWithoutChangingBaselineOrLoggingContent(string answer)
    {
        var handler = new Handler { Answer = answer };
        using var http = new HttpClient(handler);
        var store = new Store();
        var logger = new RecordingLogger();
        var baseline = Baseline();
        await Observer(http, store, logger: logger).ObserveAsync(Message(), baseline, CancellationToken.None);
        Assert.Null(store.Result);
        Assert.True(store.Failed);
        Assert.Contains(logger.Messages, text => text.Contains("AI shadow failed"));
        Assert.DoesNotContain("PRIVATE", string.Join("", logger.Messages));
        Assert.Equal(MailRoute.Forward, baseline.Route);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task HttpErrorsAreNotRetriedOrReportedAsSuccess(HttpStatusCode status)
    {
        var handler = new Handler { Status = status };
        using var http = new HttpClient(handler);
        var store = new Store();
        var logger = new RecordingLogger();
        await Observer(http, store, logger: logger).ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.Equal(1, handler.Calls);
        Assert.True(store.Failed);
        Assert.Null(store.Result);
        Assert.DoesNotContain("PRIVATE", string.Join("", logger.Messages));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    public async Task IncompleteResponsesAreNotAccepted(string finish)
    {
        using var http = new HttpClient(new Handler { Finish = finish });
        var store = new Store();
        await Observer(http, store).ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.True(store.Failed);
        Assert.Null(store.Result);
    }

    [Fact]
    public async Task StorageFailurePreventsInferenceAndIsLogged()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler);
        var store = new Store { RulesError = new Azure.RequestFailedException(403, "PRIVATE") };
        var logger = new RecordingLogger();
        await Observer(http, store, logger: logger).ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.Equal(0, handler.Calls);
        Assert.Contains(logger.Messages, text => text.Contains("AI shadow failed"));
        Assert.DoesNotContain("PRIVATE", string.Join("", logger.Messages));
    }

    [Fact]
    public async Task ShutdownCancellationIsPropagated()
    {
        using var http = new HttpClient(new Handler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Observer(http, new Store()).ObserveAsync(Message(), Baseline(), cancellation.Token));
    }

    [Fact]
    public async Task RuleVersionAndModelArePartOfDurableReference()
    {
        using var http = new HttpClient(new Handler());
        var store = new Store();
        var observer = Observer(http, store);
        await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None);
        var original = store.Reference;
        await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.Equal(original, store.Reference);
        store.Version = "version-2";
        await observer.ObserveAsync(Message(), Baseline(), CancellationToken.None);
        Assert.NotEqual(original, store.Reference);
    }

    [Theory]
    [InlineData("AiShadow:DataProcessingApproved", "false")]
    [InlineData("AiShadow:Endpoint", "http://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("AiShadow:Endpoint", "https://example.test/openai/v1/chat/completions")]
    [InlineData("AiShadow:Endpoint", "https://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions?key=secret")]
    [InlineData("AiShadow:Deployment", "")]
    [InlineData("AiShadow:StartTimeUtc", "")]
    [InlineData("AiShadow:DailyRequestLimit", "0")]
    [InlineData("AiShadow:DailyRequestLimit", "101")]
    [InlineData("AiShadow:MaximumInputCharacters", "0")]
    [InlineData("AiShadow:MaximumInputCharacters", "200001")]
    public void UnsafeConfigurationFailsBeforeNetworkAccess(string key, string value)
    {
        using var http = new HttpClient(new Handler());
        Assert.Throws<InvalidOperationException>(() => Observer(http, new Store(), configuration: Config((key, value))));
    }

    private static AiShadowObserver Observer(HttpClient http, Store store, Credential? credential = null,
        RecordingLogger? logger = null, IConfiguration? configuration = null) =>
        new(http, credential ?? new Credential(), store, configuration ?? Config(), logger ?? new RecordingLogger());

    private static IConfiguration Config(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            ["AiShadow:DataProcessingApproved"] = "true",
            ["AiShadow:StartTimeUtc"] = "2026-01-01T00:00:00.0000000+00:00",
            ["AiShadow:Endpoint"] = "https://chat-dbs-resource.openai.azure.com/openai/v1/chat/completions",
            ["AiShadow:Deployment"] = "gpt-5.4-mini"
        };
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static EmailMessage Message() => new()
    {
        Id = "PRIVATE-ID", From = "PRIVATE-FROM", Subject = "PRIVATE-SUBJECT", Body = "PRIVATE-BODY"
    };
    private static EmailDecision Baseline() => new()
    {
        Route = MailRoute.Forward, Reason = "PRIVATE-BASELINE", MatchedRules = new() { "donors" }
    };

    private sealed class Store : IAiShadowStore
    {
        public bool ClaimAvailable { get; set; } = true;
        public string Version { get; set; } = "version-1";
        public string? CatalogOverride { get; set; }
        public Exception? RulesError { get; set; }
        public int ClaimCalls { get; private set; }
        public int LastLimit { get; private set; }
        public string? Reference { get; private set; }
        public ShadowResult? Result { get; private set; }
        public bool Failed { get; private set; }
        public Task<ShadowRules> GetRulesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return RulesError is not null ? Task.FromException<ShadowRules>(RulesError) :
                Task.FromResult(new ShadowRules(CatalogOverride ?? Catalog, Version));
        }
        public Task<bool> TryClaimAsync(string reference, int dailyLimit, CancellationToken cancellationToken)
        {
            ClaimCalls++;
            LastLimit = dailyLimit;
            Reference = reference;
            return Task.FromResult(ClaimAvailable);
        }
        public Task CompleteAsync(string reference, ShadowResult result, CancellationToken cancellationToken)
        {
            Result = result;
            return Task.CompletedTask;
        }
        public Task FailAsync(string reference, CancellationToken cancellationToken)
        {
            Failed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Answer { get; set; } = """{"route":"Forward","ruleIds":["donors"],"recipientCandidates":[]}""";
        public string Finish { get; set; } = "stop";
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri!.Host == "graph.microsoft.com")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"value":[{"id":"PRIVATE-ID","subject":"PRIVATE-SUBJECT",
                        "body":{"contentType":"text","content":"PRIVATE-BODY"},
                        "receivedDateTime":"2026-10-22T09:00:00Z"}]}
                        """)
                };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK
                    ? JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Finish, message = new { content = Answer } } } })
                    : "PRIVATE-ERROR")
            };
        }
    }

    private sealed class Classifier : IMailClassifier
    {
        public Task<EmailDecision> ClassifyAsync(EmailMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(Baseline());
    }

    private sealed class Credential : TokenCredential
    {
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingLogger : ILogger<AiShadowObserver>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

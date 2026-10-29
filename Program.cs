using Azure.Identity;
using Azure.Core;
using Azure.Data.Tables;
using InfoMail;
using InfoMail.Services;

if (args.Length == 1 && args[0] == "--ai-comparison-schema")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SharePointShadowComparison.Definition()));
    return;
}

var readOnlyProbe = args.Contains("--read-only-probe", StringComparer.Ordinal);
var readOnlyExamples = args.Contains("--read-only-examples", StringComparer.Ordinal);
var azurePilot = args.Contains("--azure-read-only-pilot", StringComparer.Ordinal);
var processingPreflight = args.Contains("--processing-preflight", StringComparer.Ordinal);
var enableProcessingControls = args.Contains("--processing-enable-controls", StringComparer.Ordinal);
var aiShadowPreflight = args.Contains("--ai-shadow-preflight", StringComparer.Ordinal);
var shadowSourceInventory = args.Contains("--ai-shadow-source-inventory", StringComparer.Ordinal);
var routingFeedbackReport = args.Contains("--routing-feedback-report", StringComparer.Ordinal);
var confirmEmployeeFeedback = args.Contains("--routing-confirm-existing-human-feedback", StringComparer.Ordinal);
if (confirmEmployeeFeedback && !routingFeedbackReport)
    throw new InvalidOperationException("Employee attribution requires explicit feedback report administration.");
if (routingFeedbackReport && (!azurePilot || shadowSourceInventory || aiShadowPreflight || processingPreflight || enableProcessingControls))
    throw new InvalidOperationException("Routing feedback report requires exclusive Azure administration mode.");
if (shadowSourceInventory && (!azurePilot || aiShadowPreflight || processingPreflight || enableProcessingControls))
    throw new InvalidOperationException("Source inventory requires exclusive Azure administration mode.");
if (aiShadowPreflight && (!azurePilot || processingPreflight || enableProcessingControls))
{
    throw new InvalidOperationException("AI preflight requires Azure mode and cannot be combined with processing administration.");
}
if ((processingPreflight || enableProcessingControls) && (!azurePilot || processingPreflight && enableProcessingControls))
{
    throw new InvalidOperationException("Choose one processing administration command in Azure mode only.");
}
var builder = Host.CreateApplicationBuilder(args.Where(arg =>
    arg != "--read-only-probe" && arg != "--read-only-examples" && arg != "--azure-read-only-pilot" &&
    arg != "--processing-preflight" && arg != "--processing-enable-controls" && arg != "--ai-shadow-preflight" &&
    arg != "--ai-shadow-source-inventory" && arg != "--routing-feedback-report" &&
    arg != "--routing-confirm-existing-human-feedback").ToArray());
if (!azurePilot && builder.Configuration.GetValue("AiShadow:Enabled", false))
{
    throw new InvalidOperationException("AI shadow is supported only in Azure pilot mode.");
}

if (azurePilot)
{
    if (readOnlyProbe || readOnlyExamples)
    {
        throw new InvalidOperationException("Azure pilot mode cannot be combined with local probe modes.");
    }

    builder.Services.AddSingleton<TokenCredential>(_ =>
        new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(
            builder.Configuration["AzurePilot:ManagedIdentityClientId"]
                ?? throw new InvalidOperationException("AzurePilot:ManagedIdentityClientId is required."))));
    builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    });
    if (builder.Configuration.GetValue("SharePointRules:Enabled", false))
    {
        builder.Services.AddSingleton<SharePointRuleSource>();
        builder.Services.AddSingleton(provider => new TableClient(
            new Uri(builder.Configuration["SharePointRules:TableEndpoint"]
                ?? throw new InvalidOperationException("SharePointRules:TableEndpoint is required.")),
            "PilotState", provider.GetRequiredService<TokenCredential>()));
        builder.Services.AddSingleton(provider => new DailySharePointClassifier(
            provider.GetRequiredService<SharePointRuleSource>(), provider.GetRequiredService<TableClient>(),
            provider.GetRequiredService<ILogger<DailySharePointClassifier>>(),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "routing-catalog.json"))));
        builder.Services.AddSingleton<IMailClassifier>(provider => provider.GetRequiredService<DailySharePointClassifier>());
    }
    else
    {
        builder.Services.AddSingleton<IMailClassifier>(_ =>
            new CatalogMailClassifier(Path.Combine(AppContext.BaseDirectory, "Data", "routing-catalog.json")));
    }
    if (builder.Configuration.GetValue("AiShadow:Enabled", false) || aiShadowPreflight)
    {
        if (aiShadowPreflight)
        {
            builder.Configuration["AiShadow:DataProcessingApproved"] = "true";
            builder.Configuration["AiShadow:StartTimeUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            builder.Configuration["AiShadow:IncludeSharePointKnowledge"] = "true";
            builder.Configuration["AiShadow:IncludeExplanation"] = "true";
            builder.Configuration["AiShadow:ComparisonListEnabled"] = "true";
        }
        var coverage = builder.Configuration["AiShadow:Coverage"] ?? "Sample";
        if (coverage is not ("AllNew" or "Sample") ||
            coverage == "AllNew" && !builder.Configuration.GetValue("Processing:Enabled", false))
        {
            throw new InvalidOperationException("AI shadow coverage must be Sample or AllNew; AllNew requires the inbox processing scanner.");
        }
        if (!builder.Configuration.GetValue("SharePointRules:Enabled", false))
        {
            throw new InvalidOperationException("AI shadow requires validated SharePoint rules and persistent table state.");
        }
        builder.Services.AddKeyedSingleton<HttpClient>("AiShadow", (_, _) =>
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(25),
                MaxResponseContentBufferSize = 64 * 1024
            });
        builder.Services.AddSingleton<IAiShadowStore, TableAiShadowStore>();
        if (builder.Configuration.GetValue("AiShadow:IncludeSharePointKnowledge", false))
            builder.Services.AddSingleton<IShadowKnowledgeSource, SharePointShadowKnowledge>();
        if (builder.Configuration.GetValue("AiShadow:ComparisonListEnabled", false))
        {
            builder.Services.AddSingleton<SharePointShadowComparison>();
            builder.Services.AddSingleton<RoutingFeedbackReport>();
        }
        builder.Services.AddSingleton<SharePointShadowLog>();
        builder.Services.AddSingleton<IShadowLogQueue>(provider => provider.GetRequiredService<SharePointShadowLog>());
        builder.Services.AddSingleton<AiShadowObserver>();
    }
    builder.Services.AddSingleton(provider => new GraphReadOnlyProbe(
        provider.GetRequiredService<HttpClient>(), provider.GetRequiredService<TokenCredential>(),
        provider.GetRequiredService<IMailClassifier>(),
        builder.Configuration["AiShadow:Coverage"] == "AllNew" ? null : provider.GetService<AiShadowObserver>()));
    if (builder.Configuration.GetValue("SharePointRules:Enabled", false))
    {
        builder.Services.AddSingleton<IProcessingMailbox, GraphProcessingMailbox>();
        builder.Services.AddSingleton<IMailProcessingStore, TableMailProcessingStore>();
        builder.Services.AddSingleton<MailProcessingEngine>();
    }
    else if (builder.Configuration.GetValue("Processing:Enabled", false))
    {
        throw new InvalidOperationException("Processing requires the validated SharePoint rule source and persistent state store.");
    }
    builder.Services.AddHostedService<AzureReadOnlyPilot>();
    builder.Services.AddSingleton<ShadowSourceInventory>();
    using var pilotHost = builder.Build();
    if (routingFeedbackReport)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        if (confirmEmployeeFeedback)
        {
            var updated = await pilotHost.Services.GetRequiredService<SharePointShadowComparison>()
                .ConfirmExistingEmployeeFeedbackAsync(timeout.Token);
            Console.WriteLine($"Confirmed existing employee outcomes: {updated}. Actual actions and recipients unchanged.");
        }
        await pilotHost.Services.GetRequiredService<RoutingFeedbackReport>().RunAsync(true, timeout.Token);
        return;
    }
    if (shadowSourceInventory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await pilotHost.Services.GetRequiredService<ShadowSourceInventory>().RunAsync(timeout.Token);
        return;
    }
    if (aiShadowPreflight)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var services = pilotHost.Services;
        await services.GetRequiredService<DailySharePointClassifier>().RefreshAsync(timeout.Token);
        await services.GetRequiredService<SharePointShadowLog>().EnsureFolderAsync(timeout.Token);
        var comparisonUrl = await services.GetRequiredService<SharePointShadowComparison>().EnsureAsync(timeout.Token);
        Console.WriteLine("AI comparison list ready: " + comparisonUrl);
        var synthetic = new InfoMail.Models.EmailMessage
        {
            Id = "synthetic-shadow-" + Guid.NewGuid().ToString("N"),
            Subject = "[SYNTETISK TEST] AI-skyggelog - ingen rigtig mail",
            Body = "Dette er syntetiske testdata. Jeg vil gerne give en donation til Dansk Blindesamfund.",
            ReceivedAt = DateTimeOffset.UtcNow
        };
        var baseline = await services.GetRequiredService<IMailClassifier>().ClassifyAsync(synthetic, timeout.Token);
        if (!await services.GetRequiredService<AiShadowObserver>().ObserveAsync(synthetic, baseline, timeout.Token))
        {
            throw new InvalidOperationException("AI synthetic inference did not complete; inspect safe error logs.");
        }
        if (!await services.GetRequiredService<SharePointShadowLog>().FlushAsync(timeout.Token))
        {
            throw new InvalidOperationException("AI synthetic log export failed; activation is not verified.");
        }
        Console.WriteLine("AI shadow preflight PASSED: managed-identity inference and synthetic SharePoint log upload completed. No mailbox read or write.");
        return;
    }
    if (builder.Configuration.GetValue("AiShadow:Enabled", false))
    {
        _ = pilotHost.Services.GetRequiredService<AiShadowObserver>();
    }
    if (processingPreflight || enableProcessingControls)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var services = pilotHost.Services;
        await services.GetRequiredService<DailySharePointClassifier>().RefreshAsync(timeout.Token);
        var mailbox = services.GetRequiredService<IProcessingMailbox>();
        await mailbox.FindDestinationAsync("Videresendt via regler", timeout.Token);
        await mailbox.FindDestinationAsync(AbsenceRouting.DestinationFolder, timeout.Token);
        var store = services.GetRequiredService<IMailProcessingStore>();
        var controls = await store.GetControlsAsync(timeout.Token);
        Console.WriteLine($"Processing preflight: both existing destination folders verified. SendControl={controls.Send} MoveControl={controls.Move}. No mailbox changes.");
        if (enableProcessingControls)
        {
            await services.GetRequiredService<TableClient>().UpsertEntityAsync(new TableEntity("Control", "Forwarding")
            {
                ["SendEnabled"] = true,
                ["MoveEnabled"] = true,
                ["UpdatedUtc"] = DateTimeOffset.UtcNow
            }, TableUpdateMode.Merge, timeout.Token);
            controls = await store.GetControlsAsync(timeout.Token);
            if (!controls.Send || !controls.Move)
            {
                throw new InvalidDataException("Processing controls did not persist.");
            }
            Console.WriteLine("Processing controls verified: Send=true Move=true. Application configuration gates still apply.");
        }
        return;
    }
    await pilotHost.RunAsync();
    return;
}

if (readOnlyProbe || readOnlyExamples)
{
    try
    {
        var tenantId = builder.Configuration["GraphProbe:TenantId"];
        var clientId = builder.Configuration["GraphProbe:ClientId"];
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _))
        {
            throw new InvalidOperationException("GraphProbe:TenantId and GraphProbe:ClientId must be valid GUIDs.");
        }

        using var certificate = ProbeCertificate.Load(builder.Configuration["GraphProbe:CertificateThumbprint"]);
        var credential = new ClientCertificateCredential(tenantId, clientId, certificate);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var classifier = new RuleBasedMailClassifier(builder.Configuration,
            new RoutingDecisionService(builder.Configuration, new FolderDocumentReferenceService(builder.Configuration)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var probe = new GraphReadOnlyProbe(http, credential, classifier);
        if (readOnlyExamples)
        {
            await probe.RunExamplesAsync(Console.Out, timeout.Token);
        }
        else
        {
            await probe.RunAsync(Console.Out, timeout.Token);
        }
    }
    catch (AuthenticationFailedException)
    {
        Console.Error.WriteLine("Authentication failed. Check tenant, app, certificate upload and certificate validity. No token details are logged.");
        Environment.ExitCode = 1;
    }
    catch (HttpRequestException error)
    {
        Console.Error.WriteLine($"Graph request failed (HTTP {(int?)error.StatusCode}). Check connectivity and scoped Mail.Read access. Response content is not logged.");
        Environment.ExitCode = 1;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("The read-only test timed out or was cancelled.");
        Environment.ExitCode = 1;
    }
    catch (Exception error) when (error is InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException)
    {
        Console.Error.WriteLine($"Read-only test failed ({error.GetType().Name}). Check configuration, the current user's certificate store, and the Graph response format.");
        Environment.ExitCode = 1;
    }

    return;
}

builder.Services.Configure<InfoMail.Models.MailRobotOptions>(builder.Configuration.GetSection("MailRobot"));
builder.Services.AddSingleton<IInboxSource, JsonInboxSource>();
builder.Services.AddSingleton<IMailClassifier, RuleBasedMailClassifier>();
builder.Services.AddSingleton<RoutingDecisionService>();
builder.Services.AddSingleton<IForwardingLogStore, FileForwardingLogStore>();
builder.Services.AddSingleton<IDocumentReferenceService, FolderDocumentReferenceService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

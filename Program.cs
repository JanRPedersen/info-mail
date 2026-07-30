using InfoMail;
using InfoMail.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<InfoMail.Models.MailRobotOptions>(builder.Configuration.GetSection("MailRobot"));
builder.Services.AddSingleton<IInboxSource, JsonInboxSource>();
builder.Services.AddSingleton<IMailClassifier, RuleBasedMailClassifier>();
builder.Services.AddSingleton<RoutingDecisionService>();
builder.Services.AddSingleton<IForwardingLogStore, FileForwardingLogStore>();
builder.Services.AddSingleton<IDocumentReferenceService, FolderDocumentReferenceService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

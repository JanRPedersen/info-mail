using InfoMail.Models;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class RuleBasedMailClassifierTests
{
    [Theory]
    [InlineData("ORDRE", "", MailRoute.Forward)]
    [InlineData("", "En OrDrE", MailRoute.Forward)]
    [InlineData("sp\u00f8rgsm\u00e5l", "", MailRoute.ManualReview)]
    [InlineData("", "", MailRoute.ManualReview)]
    [InlineData("En ukendt henvendelse", "", MailRoute.ManualReview)]
    public async Task ClassifyAsync_UsesRoutingRulesOrManualReview(string subject, string body, MailRoute expected)
    {
        var classifier = CreateClassifier(new
        {
            RoutingRules = new[]
            {
                new RoutingRule { Name = "Orders", Route = "Forward", Keywords = new() { "ordre" } },
                new RoutingRule { Name = "Questions", Keywords = new() { "sp\u00f8rgsm\u00e5l" } }
            },
            ForwardRecipient = "default@example.test"
        });

        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = subject, Body = body }, CancellationToken.None);

        Assert.Equal(expected, result.Route);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.InRange(result.Confidence, 0, 1);
    }

    [Fact]
    public async Task ClassifyAsync_UsesRuleRecipientBeforeDefault()
    {
        var classifier = CreateClassifier(new
        {
            ForwardRecipient = "default@example.test",
            RoutingRules = new[]
            {
                new RoutingRule
                {
                    Name = "Orders", Route = "Forward", Keywords = new() { "ordre" },
                    Recipient = "orders@example.test"
                }
            }
        });

        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = "ordre" }, CancellationToken.None);

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal("orders@example.test", result.Recipient);
    }

    [Fact]
    public async Task ClassifyAsync_UsesDefaultRecipientWhenRoutingRuleHasNone()
    {
        var classifier = CreateClassifier(new
        {
            ForwardRecipient = "default@example.test",
            RoutingRules = new[] { new RoutingRule { Route = "Forward", Keywords = new() { "ordre" } } }
        });

        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = "ordre" }, CancellationToken.None);

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal("default@example.test", result.Recipient);
    }

    [Fact]
    public async Task ClassifyAsync_FirstMatchingRoutingRuleWins()
    {
        var classifier = CreateClassifier(new
        {
            RoutingRules = new[]
            {
                new RoutingRule { Name = "Manual", Route = "ManualReview", Keywords = new() { "ordre" } },
                new RoutingRule { Name = "Forward", Route = "Forward", Keywords = new() { "ordre" } }
            }
        });

        var result = await classifier.ClassifyAsync(new EmailMessage { Subject = "ordre" }, CancellationToken.None);

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Contains("Manual", result.Reason);
    }

    [Fact]
    public async Task ClassifyAsync_UsesForwardingRuleRecipientAndFolder()
    {
        var classifier = CreateClassifier(new
        {
            ForwardingRules = new[]
            {
                new ForwardingRule
                {
                    Name = "Support", Keywords = new() { "support" },
                    ForwardAddresses = new() { "support@example.test" }, TargetFolder = "Support"
                }
            }
        });

        var result = await classifier.ClassifyAsync(new EmailMessage { Body = "support" }, CancellationToken.None);

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal("support@example.test", result.Recipient);
        Assert.Equal("Support", result.TargetFolder);
        Assert.Contains("Support", result.Reason);
    }

    [Theory]
    [InlineData("bilag")]
    [InlineData("INVOICE")]
    [InlineData("betaling")]
    [InlineData("ordre")]
    public async Task ClassifyAsync_UsesAdministrativeFallback(string text)
    {
        var classifier = CreateClassifier(new { ForwardRecipient = "default@example.test" });

        var result = await classifier.ClassifyAsync(new EmailMessage { Body = text }, CancellationToken.None);

        Assert.Equal(MailRoute.Forward, result.Route);
        Assert.Equal("default@example.test", result.Recipient);
        Assert.Equal("Likely operational or administrative request", result.Reason);
    }

    [Theory]
    [InlineData("RoutingRule")]
    [InlineData("ForwardingRule")]
    [InlineData("Fallback")]
    public async Task ClassifyAsync_ExplicitHoldOverridesEveryForwardingPath(string path)
    {
        var classifier = CreateClassifier(new
        {
            ForwardRecipient = "default@example.test",
            RoutingRules = path == "RoutingRule"
                ? new[] { new RoutingRule { Route = "Forward", Keywords = new() { "ordre" } } }
                : Array.Empty<RoutingRule>(),
            ForwardingRules = path == "ForwardingRule"
                ? new[] { new ForwardingRule { Keywords = new() { "ordre" }, ForwardAddresses = new() { "orders@example.test" } } }
                : Array.Empty<ForwardingRule>()
        });

        var result = await classifier.ClassifyAsync(
            new EmailMessage { Subject = "ordre", Body = "ikke videresend" }, CancellationToken.None);

        Assert.Equal(MailRoute.ManualReview, result.Route);
        Assert.Null(result.Recipient);
        Assert.Null(result.TargetFolder);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task ClassifyAsync_EmptyKeywordDoesNotMatchUnrelatedMail(string keyword)
    {
        var classifier = CreateClassifier(new
        {
            RoutingRules = new[] { new RoutingRule { Route = "Forward", Keywords = new() { keyword } } }
        });

        var result = await classifier.ClassifyAsync(
            new EmailMessage { Subject = "En ukendt henvendelse" }, CancellationToken.None);

        Assert.Equal(MailRoute.ManualReview, result.Route);
    }

    private static RuleBasedMailClassifier CreateClassifier(object settings)
    {
        var configuration = TestConfiguration.Create(settings);
        return new RuleBasedMailClassifier(configuration,
            new RoutingDecisionService(configuration, new StubDocumentReferenceService()));
    }
}

using InfoMail.Models;
using InfoMail.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InfoMail.Tests;

public class RoutingDecisionServiceTests
{
    [Fact]
    public async Task EvaluateAsync_UsesConfiguredTargetFolder_WhenRuleMatches()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailRobot:ForwardingRules:0:Name"] = "Videresend",
                ["MailRobot:ForwardingRules:0:Keywords:0"] = "ordre",
                ["MailRobot:ForwardingRules:0:ForwardAddresses:0"] = "info@blind.dk",
                ["MailRobot:ForwardingRules:0:ForwardWhenAnyKeywordMatches"] = "true",
                ["MailRobot:ForwardingRules:0:TargetFolder"] = "Videresendt via regler",
                ["MailRobot:ForwardingRules:0:Enabled"] = "true"
            })
            .Build();

        var service = new RoutingDecisionService(configuration, new StubDocumentReferenceService());

        var result = await service.EvaluateAsync("ordre", "bestilling", CancellationToken.None);

        Assert.True(result.ShouldForward);
        Assert.Equal("Videresendt via regler", result.TargetFolder);
    }

    [Theory]
    [InlineData("ORDRE", "")]
    [InlineData("", "En ny OrDrE")]
    public async Task EvaluateAsync_MatchesSubjectAndBodyIgnoringCase(string subject, string body)
    {
        var service = CreateService(new ForwardingRule
        {
            Name = "Orders",
            Keywords = new() { "ordre" },
            ForwardAddresses = new() { "orders@example.test", "backup@example.test" }
        });

        var result = await service.EvaluateAsync(subject, body, CancellationToken.None);

        Assert.True(result.ShouldForward);
        Assert.Equal(new[] { "orders@example.test", "backup@example.test" }, result.Addresses);
        Assert.Contains("Orders", result.Reason);
        Assert.Contains("ordre", result.Reason);
    }

    [Theory]
    [InlineData(true, "ordre", true)]
    [InlineData(false, "ordre", false)]
    [InlineData(false, "ordre levering", true)]
    [InlineData(true, "ukendt", false)]
    public async Task EvaluateAsync_RespectsAnyAndAllKeywordMode(bool any, string body, bool expected)
    {
        var service = CreateService(new ForwardingRule
        {
            Keywords = new() { "ordre", "levering" },
            ForwardWhenAnyKeywordMatches = any,
            ForwardAddresses = new() { "orders@example.test" }
        });

        var result = await service.EvaluateAsync("", body, CancellationToken.None);

        Assert.Equal(expected, result.ShouldForward);
        if (!expected)
        {
            Assert.Empty(result.Addresses);
            Assert.Null(result.TargetFolder);
        }
    }

    [Fact]
    public async Task EvaluateAsync_IgnoresDisabledRules()
    {
        var service = CreateService(new ForwardingRule
        {
            Enabled = false,
            Keywords = new() { "ordre" },
            ForwardAddresses = new() { "orders@example.test" }
        });

        var result = await service.EvaluateAsync("ordre", "", CancellationToken.None);

        Assert.False(result.ShouldForward);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public async Task EvaluateAsync_FirstMatchingEnabledRuleWins()
    {
        var service = CreateService(
            new ForwardingRule
            {
                Name = "First", Keywords = new() { "ordre" },
                ForwardAddresses = new() { "first@example.test" }, TargetFolder = "First folder"
            },
            new ForwardingRule
            {
                Name = "Second", Keywords = new() { "ordre" },
                ForwardAddresses = new() { "second@example.test" }, TargetFolder = "Second folder"
            });

        var result = await service.EvaluateAsync("ordre", "", CancellationToken.None);

        Assert.Equal(new[] { "first@example.test" }, result.Addresses);
        Assert.Equal("First folder", result.TargetFolder);
    }

    [Fact]
    public async Task EvaluateAsync_IgnoresEmptyKeywords()
    {
        var service = CreateService(new ForwardingRule
        {
            Keywords = new() { "", " ", "\t" },
            ForwardAddresses = new() { "orders@example.test" }
        });

        var result = await service.EvaluateAsync("ukendt", "", CancellationToken.None);

        Assert.False(result.ShouldForward);
        Assert.Empty(result.Addresses);
    }

    [Theory]
    [InlineData("")]
    [InlineData("En ukendt henvendelse")]
    public async Task EvaluateAsync_NoRulesRequiresManualReview(string text)
    {
        var service = CreateService();

        var result = await service.EvaluateAsync(text, "", CancellationToken.None);

        Assert.False(result.ShouldForward);
        Assert.Empty(result.Addresses);
        Assert.Null(result.TargetFolder);
        Assert.Equal("No forwarding rule matched", result.Reason);
    }

    [Theory]
    [InlineData("IKKE VIDERESEND")]
    [InlineData("holdes tilbage")]
    [InlineData("manuel behandling")]
    [InlineData("afventer")]
    [InlineData("vurderes")]
    public async Task EvaluateAsync_ExplicitHoldOverridesMatchingForwardRule(string instruction)
    {
        var service = CreateService(new ForwardingRule
        {
            Keywords = new() { "ordre" },
            ForwardAddresses = new() { "orders@example.test" }
        });

        var result = await service.EvaluateAsync("ordre", instruction, CancellationToken.None);

        Assert.False(result.ShouldForward);
        Assert.Empty(result.Addresses);
        Assert.Null(result.TargetFolder);
        Assert.Equal("The message explicitly says it should not be forwarded", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_NoForwardRuleDoesNotProduceForwardDecision()
    {
        var service = CreateService(new ForwardingRule
        {
            Name = "Ikke videresend",
            Keywords = new() { "ikke videresend" },
            ForwardAddresses = new()
        });

        var result = await service.EvaluateAsync("", "ikke videresend", CancellationToken.None);

        Assert.False(result.ShouldForward);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public async Task EvaluateAsync_UsesDocumentReferenceTexts()
    {
        var configuration = TestConfiguration.Create(new
        {
            ForwardingRules = new[]
            {
                new ForwardingRule
                {
                    Keywords = new() { "referenceord" },
                    ForwardAddresses = new() { "orders@example.test" }
                }
            }
        });
        var service = new RoutingDecisionService(configuration, new StubDocumentReferenceService("REFERENCEORD"));

        var result = await service.EvaluateAsync("ukendt", "", CancellationToken.None);

        Assert.True(result.ShouldForward);
        Assert.Equal(new[] { "orders@example.test" }, result.Addresses);
    }

    private static RoutingDecisionService CreateService(params ForwardingRule[] rules) =>
        new(TestConfiguration.Create(new { ForwardingRules = rules }), new StubDocumentReferenceService());
}

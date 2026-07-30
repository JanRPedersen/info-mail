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

    private sealed class StubDocumentReferenceService : IDocumentReferenceService
    {
        public Task<IReadOnlyList<string>> GetReferenceTextsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}

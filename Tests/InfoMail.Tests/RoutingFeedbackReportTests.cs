using System.Text.Json;
using InfoMail.Services;
using Xunit;

namespace InfoMail.Tests;

public class RoutingFeedbackReportTests
{
    [Fact]
    public void AiAgreementAndRobotActionsAreNotHumanEvidence()
    {
        var report = RoutingFeedbackReport.Analyze(new[]
        {
            Row("1", actor: "", action: "", verdict: "Enig med AI"),
            Row("2", actor: "Regelrobot"),
            Row("3", actor: "Medarbejder", action: ""),
            Row("4", synthetic: true)
        });
        Assert.Equal(3, report.RealObservations);
        Assert.Equal(0, report.EmployeeOutcomes);
        Assert.Equal(3, report.MissingOrIncompleteOutcomes);
        Assert.Equal(1, report.RowsWithActualAction);
        Assert.Equal(3, report.RowsWithActualRecipients);
        Assert.Equal(1, report.RowsAttributedToEmployee);
        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void RealHumanForwardingProducesOnlyUnapprovedCandidateWithExamples()
    {
        var report = RoutingFeedbackReport.Analyze(new[] { Row("1"), Row("2") });
        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(2, candidate.Examples);
        Assert.Equal(2, candidate.ManualRuleExamples);
        Assert.Equal(new[] { "team@blind.dk" }, candidate.ActualRecipients);
        Assert.Equal(new[] { "1", "2" }, candidate.ComparisonRowIds);
        Assert.Equal("NeedsOwnerApprovalAndRuleTests", candidate.Status);
    }

    [Fact]
    public void VersionsOfSameMailCannotInflateEvidence()
    {
        var report = RoutingFeedbackReport.Analyze(new[]
        {
            Row("1", subject: "Same", received: "2026-10-23T09:00:00Z"),
            Row("2", subject: "Same", received: "2026-10-23T09:00:00Z")
        });
        Assert.Equal(2, report.RealObservations);
        Assert.Equal(1, report.DistinctMessages);
        Assert.Equal(1, report.EmployeeOutcomes);
        Assert.Equal(1, Assert.Single(report.Candidates).Examples);
    }

    [Fact]
    public void ContradictingHumanVersionsDoNotBecomeEvidence()
    {
        var report = RoutingFeedbackReport.Analyze(new[]
        {
            Row("1", subject: "Same", received: "2026-10-23T09:00:00Z"),
            Row("2", subject: "Same", received: "2026-10-23T09:00:00Z", to: "different@blind.dk")
        });
        Assert.Equal(1, report.ConflictingVersionOutcomes);
        Assert.Equal(0, report.EmployeeOutcomes);
        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void DifferentRuleLabelsAcrossVersionsAreNotArbitrarilyAssignedToOneTopic()
    {
        var report = RoutingFeedbackReport.Analyze(new[]
        {
            Row("1", subject: "Same", rule: "donors"),
            Row("2", subject: "Same", rule: "counselling")
        });
        Assert.Equal(1, report.ConflictingVersionOutcomes);
        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void ExplicitHoldEvidenceCannotBeAnOrdinaryExpansionCandidate()
    {
        var report = RoutingFeedbackReport.Analyze(new[] { Row("1", rule: "explicit-hold") });
        Assert.Equal("HoldOverrideRequiresSeparateReview", Assert.Single(report.Candidates).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("external@example.test")]
    [InlineData("info@blind.dk")]
    [InlineData("Invalid name")]
    public void MissingExternalOrInvalidRecipientsAreExcluded(string to)
    {
        var report = RoutingFeedbackReport.Analyze(new[] { Row("1", to: to) });
        Assert.Equal(1, report.InvalidRecipientOutcomes);
        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void ConflictingActionsAreNotHiddenByMajority()
    {
        var report = RoutingFeedbackReport.Analyze(new[]
        {
            Row("1"), Row("2"), Row("3", action: "Slettet uden videresendelse", to: ""),
            Row("4", to: "other@blind.dk")
        });
        Assert.Equal(2, report.Candidates.Length);
        Assert.All(report.Candidates, candidate => Assert.Equal("ConflictingHumanOutcomes", candidate.Status));
        Assert.Equal(2, report.Candidates[0].ConflictingExamples);
    }

    [Fact]
    public void NoTopicMatchRequiresAnExplicitMatcherRatherThanInventedRule()
    {
        var report = RoutingFeedbackReport.Analyze(new[] { Row("1", rule: "") });
        Assert.Equal("NeedsExplicitTopicAndMatcher", Assert.Single(report.Candidates).Status);
    }

    [Fact]
    public void ReportDoesNotContainSubjectsOrHumanComments()
    {
        var analysis = RoutingFeedbackReport.Analyze(new[] { Row("1", subject: "PRIVATE-SUBJECT") });
        var text = RoutingFeedbackReport.Render(analysis, new() { ["Completed"] = 3 }, new() { ["NoCatalogMatch"] = 4 });
        Assert.DoesNotContain("PRIVATE", text);
        Assert.Contains("IKKE AKTIVERET", text);
        Assert.Contains("ikke verificeret levering", text);
    }

    private static JsonElement Row(string id, string actor = "Medarbejder", string action = "Videresendt",
        string to = "team@blind.dk", bool synthetic = false, string? subject = null,
        string? received = null, string rule = "donors", string verdict = "")
    {
        return JsonSerializer.SerializeToElement(new
        {
            id,
            fields = new
            {
                IsSynthetic = synthetic, EvaluationStatus = "Completed",
                MailSubject = subject ?? "Subject " + id,
                ReceivedUtc = received ?? "2026-10-23T10:00:00Z",
                ActualActor = actor, ActualAction = action, ActualTo = to, HumanVerdict = verdict,
                HumanComment = "PRIVATE-COMMENT", RuleIds = rule,
                RuleSuggestion = "Foreslaa manuel vurdering"
            }
        });
    }
}

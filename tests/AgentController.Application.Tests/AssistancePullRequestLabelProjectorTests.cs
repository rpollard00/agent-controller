using AgentController.Application.Services;
using AgentController.Domain;

namespace AgentController.Application.Tests;

public sealed class AssistancePullRequestLabelProjectorTests
{
    private static readonly AssistancePullRequestLabels Labels = new()
    {
        Requested = "custom-assistance-requested",
        InProgress = "custom-assistance-in-progress",
        RevivalRequested = "custom-rework-requested",
    };

    [Fact]
    public void RuntimeAccepted_AddsOnlyAssistanceInProgress()
    {
        var mutation = AssistancePullRequestLabelProjector.BuildMutation(
            ReworkRequestMode.Assistance,
            AssistanceRunLabelMilestone.RuntimeAccepted,
            Labels
        );

        Assert.NotNull(mutation);
        Assert.Equal([Labels.InProgress], mutation.LabelsToAdd);
        Assert.Empty(mutation.LabelsToRemove);
    }

    [Theory]
    [InlineData(AssistanceRunLabelMilestone.BranchPushed)]
    [InlineData(AssistanceRunLabelMilestone.Completed)]
    public void SuccessfulUpdate_ClearsRequestProgressAndConflictingRevivalMarker(
        AssistanceRunLabelMilestone milestone
    )
    {
        var mutation = AssistancePullRequestLabelProjector.BuildMutation(
            ReworkRequestMode.Assistance,
            milestone,
            Labels
        );

        Assert.NotNull(mutation);
        Assert.Empty(mutation.LabelsToAdd);
        Assert.Equal(
            [Labels.Requested, Labels.InProgress, Labels.RevivalRequested],
            mutation.LabelsToRemove
        );
    }

    [Theory]
    [InlineData(AssistanceRunLabelMilestone.Failed)]
    [InlineData(AssistanceRunLabelMilestone.NeedsHuman)]
    [InlineData(AssistanceRunLabelMilestone.Cancelled)]
    public void UnsuccessfulOutcome_ClearsOnlyProgressAndRetainsRequest(
        AssistanceRunLabelMilestone milestone
    )
    {
        var mutation = AssistancePullRequestLabelProjector.BuildMutation(
            ReworkRequestMode.Assistance,
            milestone,
            Labels
        );

        Assert.NotNull(mutation);
        Assert.Empty(mutation.LabelsToAdd);
        Assert.Equal([Labels.InProgress], mutation.LabelsToRemove);
        Assert.DoesNotContain(Labels.Requested, mutation.LabelsToRemove);
    }

    [Theory]
    [InlineData(AssistanceRunLabelMilestone.RuntimeAccepted)]
    [InlineData(AssistanceRunLabelMilestone.BranchPushed)]
    [InlineData(AssistanceRunLabelMilestone.Completed)]
    [InlineData(AssistanceRunLabelMilestone.Failed)]
    [InlineData(AssistanceRunLabelMilestone.NeedsHuman)]
    [InlineData(AssistanceRunLabelMilestone.Cancelled)]
    public void RevivalRun_DoesNotMutatePullRequestLabels(
        AssistanceRunLabelMilestone milestone
    )
    {
        var mutation = AssistancePullRequestLabelProjector.BuildMutation(
            ReworkRequestMode.Revival,
            milestone,
            Labels
        );

        Assert.Null(mutation);
    }
}

namespace AgentController.Application.Tests;

public sealed class PullRequestLabelMutatorContractTests
{
    [Fact]
    public void Mutation_DefaultsToEmptyAddAndRemoveCollections()
    {
        var mutation = new PullRequestLabelMutation();

        Assert.Empty(mutation.LabelsToAdd);
        Assert.Empty(mutation.LabelsToRemove);
    }
}

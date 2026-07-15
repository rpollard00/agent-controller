using AgentController.Infrastructure;

namespace AgentController.Infrastructure.Tests;

public class BoardTerminalStatesTests
{
    [Fact]
    public void Values_ContainsExactTerminalStates()
    {
        var expected = new[] { "Closed", "Removed", "Resolved", "Completed" };

        Assert.Equal(expected, BoardTerminalStates.Values);
    }

    [Fact]
    public void Values_IsImmutable()
    {
        var list = BoardTerminalStates.Values as IList<string>;
        Assert.Throws<NotSupportedException>(() => list!.Add("Active"));
    }
}

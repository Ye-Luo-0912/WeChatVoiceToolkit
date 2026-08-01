using WeChatVoice.Workflows.Workflows;

namespace WeChatVoice.Workflows.Tests;

public sealed class WorkflowStateMachineTests
{
    [Fact]
    public void Start_complete_transitions_are_accepted()
    {
        var machine = new WorkflowStateMachine();
        var transitions = new List<WorkflowStateTransition>();
        machine.Transitioned += (_, transition) => transitions.Add(transition);

        Assert.True(machine.TryStart());
        Assert.Equal(WorkflowState.Running, machine.State);
        Assert.True(machine.TryComplete());
        Assert.Equal(WorkflowState.Completed, machine.State);
        Assert.Equal([WorkflowState.Idle, WorkflowState.Running], transitions.Select(static t => t.From).ToArray());
        Assert.Equal([WorkflowState.Running, WorkflowState.Completed], transitions.Select(static t => t.To).ToArray());
    }

    [Fact]
    public void Double_start_is_rejected()
    {
        var machine = new WorkflowStateMachine();
        Assert.True(machine.TryStart());
        Assert.False(machine.TryStart());
    }

    [Fact]
    public void Awaiting_user_round_trip_works()
    {
        var machine = new WorkflowStateMachine();
        Assert.True(machine.TryStart());
        Assert.True(machine.TryEnterAwaitingUser());
        Assert.Equal(WorkflowState.AwaitingUser, machine.State);
        Assert.True(machine.TryResumeFromUser());
        Assert.Equal(WorkflowState.Running, machine.State);
    }

    [Fact]
    public void Awaiting_user_can_be_cancelled()
    {
        var machine = new WorkflowStateMachine();
        Assert.True(machine.TryStart());
        Assert.True(machine.TryEnterAwaitingUser());
        Assert.True(machine.TryRequestCancellation());
        Assert.Equal(WorkflowState.Cancelling, machine.State);
        Assert.True(machine.TryCancel());
        Assert.Equal(WorkflowState.Cancelled, machine.State);
    }

    [Fact]
    public void Fail_then_retry_then_complete()
    {
        var machine = new WorkflowStateMachine();
        Assert.True(machine.TryStart());
        Assert.True(machine.TryFail());
        Assert.Equal(WorkflowState.Failed, machine.State);
        Assert.True(machine.TryStart());
        Assert.True(machine.TryComplete());
        Assert.Equal(WorkflowState.Completed, machine.State);
    }

    [Fact]
    public void Invalid_terminal_transitions_are_rejected()
    {
        var machine = new WorkflowStateMachine();
        Assert.True(machine.TryStart());
        Assert.True(machine.TryComplete());
        Assert.False(machine.TryFail());
        Assert.False(machine.TryCancel());
        Assert.False(machine.TryEnterAwaitingUser());
        Assert.False(machine.TryRequestCancellation());
    }
}

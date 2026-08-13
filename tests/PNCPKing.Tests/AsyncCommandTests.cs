using PNCPKing.App.ViewModels;

namespace PNCPKing.Tests;

public sealed class AsyncCommandTests
{
    [Fact]
    public async Task RapidExecution_RunsOnceAndReportsRejectedAttempts()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var rejected = 0;
        AsyncCommandRuntime.Configure(
            _ => { },
            () => Interlocked.Increment(ref rejected));
        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await release.Task;
        });

        for (var index = 0; index < 20; index++)
        {
            command.Execute(null);
        }

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(command.IsRunning);
        Assert.Equal(1, runs);
        Assert.Equal(19, rejected);

        release.TrySetResult();
        await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task RecoverableException_IsContainedAndReported()
    {
        Exception? reported = null;
        AsyncCommandRuntime.Configure(exception => reported = exception);
        var command = new AsyncRelayCommand(
            () => Task.FromException(new InvalidOperationException("falha controlada")));

        command.Execute(null);
        await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(1));

        var exception = Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal("falha controlada", exception.Message);
        Assert.False(command.IsRunning);
    }
}

using PNCPKing.Infrastructure.Data;

namespace PNCPKing.Tests;

public sealed class SqliteWorkCoordinatorTests
{
    [Fact]
    public async Task VisibleWriter_IsServedBeforeQueuedBackgroundWriter()
    {
        var coordinator = new SqliteWorkCoordinator();
        await using var active = await coordinator.EnterWriterAsync(SqliteWorkPriority.Background);
        var background = coordinator.EnterWriterAsync(SqliteWorkPriority.Background).AsTask();
        var visible = coordinator.EnterWriterAsync(SqliteWorkPriority.Visible).AsTask();

        await active.DisposeAsync();
        await using var visibleLease = await visible.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(background.IsCompleted);
        await visibleLease.DisposeAsync();
        await using var backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReaderLimit_IsTwo()
    {
        var coordinator = new SqliteWorkCoordinator();
        await using var first = await coordinator.EnterReaderAsync();
        await using var second = await coordinator.EnterReaderAsync();
        var third = coordinator.EnterReaderAsync().AsTask();

        Assert.False(third.IsCompleted);
        await first.DisposeAsync();
        await using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(1));
    }
}

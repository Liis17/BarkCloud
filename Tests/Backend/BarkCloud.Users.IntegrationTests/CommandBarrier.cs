using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BarkCloud.Users.IntegrationTests;

public sealed class CommandBarrier(Func<string, bool> matches, int participants, bool beforeExecution = false)
    : DbCommandInterceptor
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivals;

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (beforeExecution)
            await WaitAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (!beforeExecution)
            await WaitAsync(command, cancellationToken);
        return result;
    }

    private async Task WaitAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!matches(command.CommandText))
            return;
        if (Interlocked.Increment(ref _arrivals) == participants)
            _reached.TrySetResult();
        await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }
}

using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BarkCloud.Identity.IntegrationTests;

internal sealed class ResetReadBarrier : DbCommandInterceptor
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivals;

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public void Release() => _released.TrySetResult();

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM \"ResetPasswords\""))
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _reached.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}

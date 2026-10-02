using System.Data.Common;

using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BarkCloud.Identity.IntegrationTests;

/// <summary>Останавливает запрос после чтения refresh-токена: моделирует CreateToken, читающий строку до сброса пароля.</summary>
internal sealed class RefreshReadPause : DbCommandInterceptor
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public void Release() => _released.TrySetResult();

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FROM \"RefreshTokens\""))
        {
            _reached.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}

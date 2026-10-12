using BarkCloud.Identity.Persistence.Contexts;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace BarkCloud.Identity.Persistence.Services;

/// <summary>Сериализует операции регистрации одного владельца на всех репликах Identity.</summary>
public static class IdentityOwnerLock
{
    public static async Task AcquireAsync(IdentityContext context, long ownerId,
        CancellationToken cancellationToken = default)
    {
        if (!context.Database.IsNpgsql())
            return;

        var transaction = context.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Для блокировки владельца нужна активная транзакция.");
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(@owner_id)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "owner_id";
        parameter.Value = ownerId;
        command.Parameters.Add(parameter);

        await command.ExecuteScalarAsync(cancellationToken);
    }
}

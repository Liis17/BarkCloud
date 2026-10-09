using System.Data.Common;

using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BarkCloud.Identity.IntegrationTests;

public class EmailAuthCodeSnapshotTests
{
    [Fact]
    public async Task ReissueBetweenSnapshotReadAndReservationUpdate_DoesNotReserveNewIssuance()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = 42,
                LastEmailAuthCode = "123456",
                EmailAuthCodePurpose = EmailAuthCodePurpose.Login,
                EmailAuthCodeIssuedAt = DateTime.UtcNow,
                EmailAuthCodeExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext(new ReissueAfterSnapshotReadInterceptor(database));
        var validated = await new AuthPropertiesStorage(context)
            .TryValidateAndReserveEmailAuthCode(42, EmailAuthCodePurpose.Login, "123456");

        validated.Should().BeNull();
        await using var observer = database.CreateContext();
        var properties = await observer.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == 42);
        properties.LastEmailAuthCode.Should().Be("654321");
        properties.EmailAuthCodeAttempts.Should().Be(0);
    }

    private sealed class ReissueAfterSnapshotReadInterceptor(PostgresIdentityDatabase database) : DbCommandInterceptor
    {
        private bool _reissued;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_reissued && command.CommandText.Contains("\"AuthUserProperties\"", StringComparison.Ordinal))
            {
                _reissued = true;
                await using var concurrent = database.CreateContext();
                var issuedAt = DateTime.UtcNow.AddSeconds(1);
                await concurrent.AuthUserProperties.Where(x => x.UserId == 42)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.LastEmailAuthCode, "654321")
                        .SetProperty(x => x.EmailAuthCodeIssuedAt, (DateTime?)issuedAt)
                        .SetProperty(x => x.EmailAuthCodeExpiresAt, (DateTime?)(issuedAt.AddMinutes(5)))
                        .SetProperty(x => x.EmailAuthCodeAttempts, 0), cancellationToken);
            }

            return result;
        }
    }
}

using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.IntegrationTests;

public sealed class TotpReplayAtomicityTests
{
    private const long UserId = 42;
    private const string Secret = "JBSWY3DPEHPK3PXP";

    [Fact]
    public async Task SameStepAcrossIndependentContexts_HasOneWinner()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = UserId, OtpEnabled = true, OtpSecret = Secret, OtpSecretGeneration = 3
            });
            await setup.SaveChangesAsync();
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempt = Attempt(first, start.Task);
        var secondAttempt = Attempt(second, start.Task);
        start.SetResult();

        var results = await Task.WhenAll(firstAttempt, secondAttempt);

        results.Count(x => x).Should().Be(1);
        await using var reader = database.CreateContext();
        (await reader.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId))
            .LastAcceptedTotpStep.Should().Be(987654);
    }

    [Fact]
    public async Task StepRollbackAndSecretRotation_PreserveGenerationBoundary()
    {
        await using var database = await PostgresIdentityDatabase.CreateAsync();
        await using (var setup = database.CreateContext())
        {
            setup.AuthUserProperties.Add(new AuthUserProperty
            {
                UserId = UserId, OtpEnabled = true, OtpSecret = Secret,
                OtpSecretGeneration = 3, LastAcceptedTotpStep = 100
            });
            await setup.SaveChangesAsync();
        }

        await using (var rollbackContext = database.CreateContext())
        {
            await using var transaction = await rollbackContext.Database.BeginTransactionAsync();
            (await new AuthPropertiesStorage(rollbackContext)
                .TryConsumeTotpStep(UserId, Secret, 3, 101)).Should().BeTrue();
            await transaction.RollbackAsync();
        }

        const string rotatedSecret = "MFRGGZDFMZTWQ2LK";
        await using (var rotate = database.CreateContext())
        {
            await rotate.AuthUserProperties.Where(x => x.UserId == UserId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.OtpSecret, rotatedSecret)
                    .SetProperty(x => x.OtpSecretGeneration, 4)
                    .SetProperty(x => x.LastAcceptedTotpStep, (long?)null));
        }

        await using var retry = database.CreateContext();
        var storage = new AuthPropertiesStorage(retry);
        (await storage.TryConsumeTotpStep(UserId, Secret, 3, 101)).Should().BeFalse();
        (await storage.TryConsumeTotpStep(UserId, rotatedSecret, 4, 101)).Should().BeTrue();
        (await storage.TryConsumeTotpStep(UserId, rotatedSecret, 4, 101)).Should().BeFalse();
    }

    private static async Task<bool> Attempt(Identity.Persistence.Contexts.IdentityContext context, Task start)
    {
        await start;
        return await new AuthPropertiesStorage(context).TryConsumeTotpStep(UserId, Secret, 3, 987654);
    }
}

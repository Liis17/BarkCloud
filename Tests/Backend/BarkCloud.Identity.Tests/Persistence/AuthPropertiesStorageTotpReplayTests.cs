using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Tests._Helpers;

using Microsoft.EntityFrameworkCore;

namespace BarkCloud.Identity.Tests.Persistence;

public sealed class AuthPropertiesStorageTotpReplayTests : IDisposable
{
    private const long UserId = 7;
    private const string Secret = "JBSWY3DPEHPK3PXP";
    private readonly SqliteIdentityContext _db = new();

    public void Dispose() => _db.Dispose();

    private async Task SeedActive(string secret = Secret, long generation = 3, long? lastStep = null)
    {
        _db.Context.AuthUserProperties.Add(new AuthUserProperty
        {
            UserId = UserId,
            OtpEnabled = true,
            OtpSecret = secret,
            OtpSecretGeneration = generation,
            LastAcceptedTotpStep = lastStep
        });
        await _db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Consume_AcceptsEachStepOnceAndRequiresCurrentSecretGeneration()
    {
        await SeedActive();
        var storage = new AuthPropertiesStorage(_db.Context);

        (await storage.TryConsumeTotpStep(UserId, Secret, 3, 100)).Should().BeTrue();
        (await storage.TryConsumeTotpStep(UserId, Secret, 3, 100)).Should().BeFalse();
        (await storage.TryConsumeTotpStep(UserId, Secret, 3, 99)).Should().BeFalse();
        (await storage.TryConsumeTotpStep(UserId, "OTHERSECRET", 3, 101)).Should().BeFalse();
        (await storage.TryConsumeTotpStep(UserId, Secret, 2, 101)).Should().BeFalse();
        (await storage.TryConsumeTotpStep(UserId, Secret, 3, 101)).Should().BeTrue();
    }

    [Fact]
    public async Task Consume_OnlyOneIndependentContextCanAcceptTheSameStep()
    {
        await SeedActive();
        using var first = _db.CreateAdditionalContext();
        using var second = _db.CreateAdditionalContext();
        var storages = new[] { new AuthPropertiesStorage(first), new AuthPropertiesStorage(second) };

        var accepted = await Task.WhenAll(storages.Select(storage =>
            storage.TryConsumeTotpStep(UserId, Secret, 3, 123)));

        accepted.Count(value => value).Should().Be(1);
    }

    [Fact]
    public async Task Consume_RollsBackWithProtectedTransaction()
    {
        await SeedActive();
        using (var context = _db.CreateAdditionalContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            (await new AuthPropertiesStorage(context).TryConsumeTotpStep(UserId, Secret, 3, 321)).Should().BeTrue();
            await transaction.RollbackAsync();
        }

        using var retryContext = _db.CreateAdditionalContext();
        (await new AuthPropertiesStorage(retryContext).TryConsumeTotpStep(UserId, Secret, 3, 321)).Should().BeTrue();
    }

    [Fact]
    public async Task ActivatePendingSecret_StoresTheSetupCodeStepForTheNewGeneration()
    {
        _db.Context.AuthUserProperties.Add(new AuthUserProperty
        {
            UserId = UserId,
            OtpSecretGeneration = 8,
            SelectedOtpType = OtpType.Authenticator,
            PendingOtpSecret = Secret,
            PendingOtpSecretExpiresAt = DateTime.UtcNow.AddMinutes(5),
            PendingOtpSecurityStateVersion = 0
        });
        await _db.Context.SaveChangesAsync();
        var acceptedStep = 654321L;

        (await new AuthPropertiesStorage(_db.Context).ActivatePendingOtpSecret(
            UserId, Secret, acceptedTotpStep: acceptedStep)).Should().BeTrue();

        using var verify = _db.CreateAdditionalContext();
        var props = await verify.AuthUserProperties.AsNoTracking().SingleAsync(x => x.UserId == UserId);
        props.OtpSecretGeneration.Should().Be(9);
        props.LastAcceptedTotpStep.Should().Be(acceptedStep);
        (await new AuthPropertiesStorage(verify).TryConsumeTotpStep(UserId, Secret, 9, acceptedStep))
            .Should().BeFalse();
        (await new AuthPropertiesStorage(verify).TryConsumeTotpStep(UserId, Secret, 9, acceptedStep + 1))
            .Should().BeTrue();
    }

    [Fact]
    public void Verify_CorruptOrMismatchedSecretReturnsNoCode()
    {
        BarkCloud.Identity.Services.TotpVerificationService.Verify(UserId, "!!not-base32!!", 1, "123456")
            .Should().BeNull();
        BarkCloud.Identity.Services.TotpVerificationService.Verify(UserId, Secret, 1, "123456")
            .Should().BeNull();
    }
}

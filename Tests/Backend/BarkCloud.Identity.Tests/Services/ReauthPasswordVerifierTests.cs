using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Shared.Exceptions.Identity;

namespace BarkCloud.Identity.Tests.Services;

public class ReauthPasswordVerifierTests
{
    private const long UserId = 42;
    private const string Password = "correct-password";
    private static readonly string PasswordHash = PasswordHasher.HashPassword(Password);

    private readonly Mock<IAuthPropertiesStorage> _authProps = new();
    private readonly Mock<IPasswordsStorage> _passwords = new();

    public ReauthPasswordVerifierTests()
    {
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(UserId)).ReturnsAsync(true);
        _passwords.Setup(p => p.GetUserPasswordHash(UserId)).ReturnsAsync(PasswordHash);
    }

    private ReauthPasswordVerifier CreateSut() => new(_authProps.Object, _passwords.Object);

    [Fact]
    public async Task Verify_CorrectPassword_ReservesAttemptAndResetsCounter()
    {
        await CreateSut().VerifyAsync(UserId, Password);

        _authProps.Verify(s => s.TryReserveReauthPasswordAttempt(UserId), Times.Once);
        _authProps.Verify(s => s.ResetReauthPasswordAttempts(UserId), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Verify_EmptyPassword_ThrowsWithoutConsumingAttempt(string? password)
    {
        var act = () => CreateSut().VerifyAsync(UserId, password);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.TryReserveReauthPasswordAttempt(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Verify_WrongPassword_ThrowsKeepsAttemptConsumed()
    {
        var act = () => CreateSut().VerifyAsync(UserId, "wrong-password");

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.TryReserveReauthPasswordAttempt(UserId), Times.Once);
        _authProps.Verify(s => s.ResetReauthPasswordAttempts(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Verify_NoPasswordHash_Throws()
    {
        _passwords.Setup(p => p.GetUserPasswordHash(UserId)).ReturnsAsync((string?)null);

        var act = () => CreateSut().VerifyAsync(UserId, Password);

        await act.Should().ThrowAsync<InvalidPasswordException>();
        _authProps.Verify(s => s.ResetReauthPasswordAttempts(It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task Verify_LimitExceeded_ThrowsWithoutCheckingPassword()
    {
        _authProps.Setup(s => s.TryReserveReauthPasswordAttempt(UserId)).ReturnsAsync(false);

        // Даже верный пароль не принимается, пока лимит окна не освободится.
        var act = () => CreateSut().VerifyAsync(UserId, Password);

        await act.Should().ThrowAsync<PasswordAttemptsExceededException>();
        _passwords.Verify(p => p.GetUserPasswordHash(It.IsAny<long>()), Times.Never);
        _authProps.Verify(s => s.ResetReauthPasswordAttempts(It.IsAny<long>()), Times.Never);
    }
}

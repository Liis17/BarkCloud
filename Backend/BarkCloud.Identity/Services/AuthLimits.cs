namespace BarkCloud.Identity.Services;

/// <summary>
/// Политика ограничений попыток и отправки писем (F14). Окно открывается первой попыткой;
/// <see cref="Policy.Scope"/> входит в ключ счётчика, поэтому разные операции не делят лимит.
/// </summary>
public static class AuthLimits
{
    public readonly record struct Policy(string Scope, int Max, TimeSpan Window);

    private static readonly TimeSpan Short = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Resend = TimeSpan.FromSeconds(60);

    // По источнику запроса (доверенный IP).
    public static readonly Policy AuthByIp = new("auth-ip", 60, Short);
    public static readonly Policy ResetPasswordByIp = new("reset-ip", 10, Hour);
    public static readonly Policy ConfirmResetPasswordByIp = new("reset-confirm-ip", 30, Short);
    public static readonly Policy CreateAccountByIp = new("register-ip", 10, Hour);
    public static readonly Policy ConfirmAccountByIp = new("register-confirm-ip", 30, Short);
    public static readonly Policy WebAuthnByIp = new("webauthn-ip", 60, Short);

    // По аккаунту / получателю письма.
    public static readonly Policy LoginByAccount = new("login", 10, Short);
    public static readonly Policy OtpByAccount = new("otp", 10, Short);
    public static readonly Policy FailedLoginMail = new("failed-login-mail", 1, Short);
    public static readonly Policy ResetMailCooldown = new("reset-mail-cooldown", 1, Resend);
    public static readonly Policy ResetMailHourly = new("reset-mail-hourly", 5, Hour);
    public static readonly Policy RegistrationMailCooldown = new("register-mail-cooldown", 1, Resend);
    public static readonly Policy RegistrationMailHourly = new("register-mail-hourly", 5, Hour);

    /// <summary>Неверных попыток ввода на один код (сброс пароля, регистрация).</summary>
    public const int ChallengeMaxAttempts = 5;
}

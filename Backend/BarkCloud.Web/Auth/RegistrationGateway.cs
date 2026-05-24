using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Auth;

/// <summary>
/// Регистрация веб-пользователя без подтверждения по почте и без 2FA.
/// Собирает подтверждённый аккаунт через серверные (inter-service) API и сразу
/// открывает сессию: AddDraftUser → ConfirmUser → ForceSetPasswordServer → CreateSessionForUserServer.
/// Вызовы авторизуются сервисным токеном (см. <see cref="ServiceToken"/>).
/// </summary>
public sealed class RegistrationGateway
{
    private const int MinPasswordLength = 6;

    private readonly UsersServerApi.UsersServerApiClient _users;
    private readonly IdentityServerApi.IdentityServerApiClient _identity;
    private readonly AuthGateway _auth;
    private readonly string _appName;
    private readonly string _appVersion;
    private readonly ILogger<RegistrationGateway> _logger;

    public RegistrationGateway(
        UsersServerApi.UsersServerApiClient users,
        IdentityServerApi.IdentityServerApiClient identity,
        AuthGateway auth,
        IConfiguration configuration,
        ILogger<RegistrationGateway> logger)
    {
        _users = users;
        _identity = identity;
        _auth = auth;
        _appName = configuration.Value("App:AppName", "BarkCloud Web");
        _appVersion = configuration.Value("App:Version", "v1.0.0");
        _logger = logger;
    }

    public async Task<RegistrationResult> RegisterAsync(
        HttpContext http, string firstName, string lastName, string username, string email, string password)
    {
        username = username.Trim();
        email = email.Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(email))
            return new RegistrationResult(RegistrationOutcome.ValidationError, "Заполните юзернейм и почту.");

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return new RegistrationResult(RegistrationOutcome.ValidationError,
                $"Пароль должен быть не короче {MinPasswordLength} символов.");

        try
        {
            if ((await _users.CheckExistUsernameAsync(new CheckExistUsernameRequest { Username = username })).Exist)
                return new RegistrationResult(RegistrationOutcome.UsernameTaken, "Этот юзернейм уже занят.");

            if ((await _users.CheckExistEmailAsync(new CheckExistEmailRequest { Email = email })).Exist)
                return new RegistrationResult(RegistrationOutcome.EmailTaken, "Аккаунт с такой почтой уже существует.");

            var draft = new AddDraftUserRequest
            {
                FirstName = firstName.Trim(),
                LastName = lastName.Trim(),
                Username = username,
                Email = email
            };

            long userId;
            try
            {
                userId = (await _users.AddDraftUserAsync(draft)).UserId;
            }
            catch (RpcException ex)
            {
                // черновик мог остаться от незавершённой попытки — переопределяем его данные
                _logger.LogInformation("AddDraftUser не прошёл ({Status}), пробую OverrideDraftUser", ex.StatusCode);
                userId = (await _users.OverrideDraftUserAsync(draft)).UserId;
            }

            await _users.ConfirmUserAsync(new ConfirmUserRequest { UserId = userId });

            await _identity.ForceSetPasswordServerAsync(new ForceSetPasswordServerRequest
            {
                UserId = userId,
                NewPassword = password
            });

            var deviceId = _auth.GetOrCreateDeviceId(http);
            var device = BrowserContext.BuildDeviceInfo(http, deviceId, _appName, _appVersion);

            var session = await _identity.CreateSessionForUserServerAsync(new CreateSessionForUserServerRequest
            {
                UserId = userId,
                DeviceId = deviceId,
                DeviceName = device.DeviceName,
                OperationSystem = device.Os,
                AppName = $"{device.AppName} v.{device.AppVersion}",
                IpAddress = device.Ip
            });

            _auth.IssueSession(http, session.AccessToken, session.RefreshToken, persistent: true);

            _logger.LogInformation("Зарегистрирован пользователь {UserId} ({Username})", userId, username);
            return new RegistrationResult(RegistrationOutcome.Success);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Регистрация не выполнена: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
            return new RegistrationResult(RegistrationOutcome.Error, "Не удалось создать аккаунт. Попробуйте позже.");
        }
    }
}

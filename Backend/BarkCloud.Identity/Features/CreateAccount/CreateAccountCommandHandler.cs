using System.Security.Cryptography;
using System.Text;

using BarkCloud.GrpcServer;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.Tracker;
using BarkCloud.Identity.Domain;
using BarkCloud.Identity.Infrastructure;
using BarkCloud.Identity.Persistence.Contexts;
using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Identity.Services;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Shared.Exceptions.Identity;
using BarkCloud.Shared.Exceptions.Users;
using BarkCloud.Shared.Identity;
using BarkCloud.Shared.Queue.Notifications;

using Google.Protobuf.WellKnownTypes;

using MediatR;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Identity.Features.CreateAccount;

public class CreateAccountCommandHandler(UsersServerApi.UsersServerApiClient usersClient,
    IConfirmationCodesStorage confirmationCodesStorage, NotificationQueueSender notificationQueueSender,
    RequestContext requestContext, LocationClient locationClient, MetricsCollector metrics,
    IRefreshTokensStorage refreshTokensStorage, IPasswordsStorage passwordsStorage, IdentityContext context,
    IConfiguration configuration, IRegistrationPolicy registrationPolicy, IAuthRateLimiter rateLimiter,
    ILogger<CreateAccountCommandHandler> logger)
    : IRequestHandler<CreateAccountCommand, CreateAccountResponse>
{
    private const int ExpDaysRefreshToken = 9999;
    private static readonly TimeSpan UsersCallTimeout = TimeSpan.FromSeconds(10);

    public async Task<CreateAccountResponse> Handle(CreateAccountCommand request, CancellationToken cancellationToken)
    {
        if (request.RequiresInitialPassword)
            ValidateInitialPassword(request.InitialPassword);

        logger.LogInformation("Начало создания аккаунта. Username: {Username}, Email: {Email}",
            request.Username, request.Email);

        if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Username))
            throw new UsernameOrEmailIsEmptyException();
        if (string.IsNullOrEmpty(requestContext.DeviceName))
            throw new XDeviceNameIsRequiredException();
        if (string.IsNullOrEmpty(requestContext.OperationSystem))
            throw new XOsNameIsRequiredException();
        if (string.IsNullOrEmpty(requestContext.AppName) || string.IsNullOrEmpty(requestContext.AppVersion))
            throw new XAppInfoIsRequiedException();

        await registrationPolicy.EnsureRegistrationEnabledAsync(cancellationToken);
        await rateLimiter.EnsureSourceAsync(AuthLimits.CreateAccountByIp);

        var userRequest = new AddDraftUserRequest
        {
            Email = request.Email.Trim(),
            Username = request.Username.Trim(),
            FirstName = request.FirstName?.Trim(),
            LastName = request.LastName?.Trim()
        };

        AddDraftUserResponse owner;
        var existingDraft = false;
        try
        {
            owner = await usersClient.AddDraftUserAsync(userRequest, cancellationToken: cancellationToken);
            metrics.Increment("accounts_drafted");
            logger.LogDebug("Черновик пользователя создан. UserId: {UserId}", owner.UserId);
        }
        catch (UserIsDraftException)
        {
            existingDraft = true;
            var found = await usersClient.FindByLoginAsync(
                new FindByLoginRequest { Email = request.Email.Trim() }, cancellationToken: cancellationToken);
            owner = new AddDraftUserResponse { UserId = found.User.Id };
            logger.LogDebug("Найден существующий черновик пользователя {UserId}", owner.UserId);
        }

        if (configuration.EmailEnabled())
        {
            // Защищённый черновик восстанавливается без отправки нового письма и независимо от настройки SMTP.
            if (existingDraft && !string.IsNullOrEmpty(await passwordsStorage.GetUserPasswordHash(owner.UserId, cancellationToken)))
            {
                if (!request.RequiresInitialPassword)
                    throw new UserIsDraftException();
                return await RecoverProtectedDraft(request, owner.UserId, cancellationToken);
            }

            await ReserveRegistrationMailLimits(request.Email);
            return await CreateEmailEnabledDraft(request, userRequest, owner, existingDraft, cancellationToken);
        }

        return await CreateEmailDisabledAccount(request, userRequest, owner, existingDraft, cancellationToken);
    }

    private async Task<CreateAccountResponse> CreateEmailEnabledDraft(CreateAccountCommand request,
        AddDraftUserRequest userRequest, AddDraftUserResponse owner, bool existingDraft,
        CancellationToken cancellationToken)
    {
        ConfirmationCode confirmationCode;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await IdentityOwnerLock.AcquireAsync(context, owner.UserId, cancellationToken);
            var existingHash = await passwordsStorage.GetUserPasswordHash(owner.UserId, cancellationToken);
            if (!string.IsNullOrEmpty(existingHash))
            {
                if (!request.RequiresInitialPassword)
                    throw new UserIsDraftException();

                // Another request may have protected the draft after the preflight read.
                if (!await ReserveRecoveryAttempt(owner.UserId))
                {
                    await transaction.CommitAsync(cancellationToken);
                    throw new PasswordAttemptsExceededException();
                }
                if (!PasswordHasher.VerifyPassword(request.InitialPassword!, existingHash))
                {
                    await transaction.CommitAsync(cancellationToken);
                    throw new InvalidLoginOrPasswordException();
                }

                try
                {
                    await ConfirmUserAsync(owner.UserId, cancellationToken);
                }
                catch
                {
                    await transaction.CommitAsync(CancellationToken.None);
                    throw;
                }
                await rateLimiter.ResetAsync(AuthLimits.LoginByAccount, owner.UserId.ToString(), cancellationToken);
                var refresh = await CreateRefreshToken(owner.UserId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                metrics.Increment("accounts_confirmed");
                metrics.Increment("sessions_created");
                metrics.Increment("accounts_draft_recovered");
                return ConfirmedResponse(refresh);
            }

            if (existingDraft)
                await OverrideDraftAsync(userRequest, cancellationToken);

            var code = CodeGenerator.GenerateDigitalCode(6);
            confirmationCode = await confirmationCodesStorage.AddCode(new ConfirmationCode
            {
                Expires = DateTime.UtcNow.AddHours(6),
                OwnerId = owner.UserId,
                Type = ConfirmationCodeType.Registration,
                Value = code
            }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            context.ChangeTracker.Clear();
            throw;
        }

        var confirmationCodeValue = confirmationCode.Value;
        var locationInfo = await locationClient.GetLocationString(requestContext.IpAddress);
        await notificationQueueSender.SendNotification(new EmailNotification
        {
            Address = request.Email,
            CreatedAt = DateTime.UtcNow,
            OwnerId = owner.UserId,
            ServiceId = ServiceId.Identity,
            Payload = new Dictionary<string, string>
            {
                ["confirmation_code"] = confirmationCodeValue,
                ["username"] = request.Username,
                ["ip"] = requestContext.IpAddress ?? string.Empty,
                ["devicename"] = requestContext.DeviceName,
                ["os"] = requestContext.OperationSystem,
                ["location"] = locationInfo,
                ["appname"] = $"{requestContext.AppName} v.{requestContext.AppVersion}",
                ["datetime"] = DateTime.UtcNow.ToString("dd.MM.yyyy HH:mm:ss")
            },
            Title = "Код подтверждения",
            Type = NotificationType.ConfirmationRegistration
        });

        logger.LogInformation("Аккаунт создан. UserId: {UserId}, Username: {Username}, CodeId: {CodeId}",
            owner.UserId, request.Username, confirmationCode.Id);
        return new CreateAccountResponse { CodeId = confirmationCode.Id.ToString() };
    }

    private async Task<CreateAccountResponse> CreateEmailDisabledAccount(CreateAccountCommand request,
        AddDraftUserRequest userRequest, AddDraftUserResponse owner, bool existingDraft,
        CancellationToken cancellationToken)
    {
        var refreshToken = RefreshTokenGenerator.GenerateRefreshToken();
        var protectedDraft = false;
        var confirmedUnderLock = false;

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await IdentityOwnerLock.AcquireAsync(context, owner.UserId, cancellationToken);
                var existingHash = await passwordsStorage.GetUserPasswordHash(owner.UserId, cancellationToken);
                if (!string.IsNullOrEmpty(existingHash))
                {
                    if (!request.RequiresInitialPassword)
                        throw new UserIsDraftException();

                    protectedDraft = true;
                    if (!await ReserveRecoveryAttempt(owner.UserId))
                    {
                        await transaction.CommitAsync(cancellationToken);
                        throw new PasswordAttemptsExceededException();
                    }
                    if (!PasswordHasher.VerifyPassword(request.InitialPassword!, existingHash))
                    {
                        await transaction.CommitAsync(cancellationToken);
                        throw new InvalidLoginOrPasswordException();
                    }
                    try
                    {
                        await ConfirmUserAsync(owner.UserId, cancellationToken);
                    }
                    catch
                    {
                        await transaction.CommitAsync(CancellationToken.None);
                        throw;
                    }
                    await rateLimiter.ResetAsync(AuthLimits.LoginByAccount, owner.UserId.ToString(), cancellationToken);
                    confirmedUnderLock = true;
                }
                else
                {
                    if (existingDraft)
                        await OverrideDraftAsync(userRequest, cancellationToken);

                    if (request.RequiresInitialPassword)
                    {
                        var hash = PasswordHasher.HashPassword(request.InitialPassword!);
                        if (!await passwordsStorage.CreateInitialPassword(owner.UserId, hash, cancellationToken))
                            throw new UserIsDraftException();
                    }
                }

                await refreshTokensStorage.CreateNewRefreshToken(refreshToken, owner.UserId,
                    requestContext.DeviceId ?? requestContext.DeviceName!, ExpDaysRefreshToken, cancellationToken);

                // Для новой парольной регистрации локальные credentials фиксируются до Users.ConfirmUser.
                // Legacy flow выполняет Users RPC под тем же owner lock, затем фиксирует refresh.
                if (!request.RequiresInitialPassword)
                {
                    await ConfirmUserAsync(owner.UserId, cancellationToken);
                    confirmedUnderLock = true;
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                context.ChangeTracker.Clear();
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
                throw;
            }
        }

        if (request.RequiresInitialPassword && !confirmedUnderLock)
            await ConfirmUserAsync(owner.UserId, cancellationToken);

        metrics.Increment("accounts_confirmed");
        metrics.Increment("sessions_created");
        if (protectedDraft)
            metrics.Increment("accounts_draft_recovered");
        return ConfirmedResponse(refreshToken);
    }

    private async Task<CreateAccountResponse> RecoverProtectedDraft(CreateAccountCommand request, long ownerId,
        CancellationToken cancellationToken)
    {
        var refreshToken = RefreshTokenGenerator.GenerateRefreshToken();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await IdentityOwnerLock.AcquireAsync(context, ownerId, cancellationToken);
            var currentHash = await passwordsStorage.GetUserPasswordHash(ownerId, cancellationToken);
            if (string.IsNullOrEmpty(currentHash))
                throw new UserIsDraftException();
            if (!await ReserveRecoveryAttempt(ownerId))
            {
                await transaction.CommitAsync(cancellationToken);
                throw new PasswordAttemptsExceededException();
            }
            if (!PasswordHasher.VerifyPassword(request.InitialPassword!, currentHash))
            {
                await transaction.CommitAsync(cancellationToken);
                throw new InvalidLoginOrPasswordException();
            }

            try
            {
                await ConfirmUserAsync(ownerId, cancellationToken);
            }
            catch
            {
                await transaction.CommitAsync(CancellationToken.None);
                throw;
            }
            await rateLimiter.ResetAsync(AuthLimits.LoginByAccount, ownerId.ToString(), cancellationToken);
            await refreshTokensStorage.CreateNewRefreshToken(refreshToken, ownerId,
                requestContext.DeviceId ?? requestContext.DeviceName!, ExpDaysRefreshToken, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            context.ChangeTracker.Clear();
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
        metrics.Increment("accounts_confirmed");
        metrics.Increment("sessions_created");
        metrics.Increment("accounts_draft_recovered");
        return ConfirmedResponse(refreshToken);
    }

    private async Task ReserveRegistrationMailLimits(string email)
    {
        var recipient = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
        if (!await rateLimiter.TryReserveAsync(AuthLimits.RegistrationMailCooldown, recipient)
            || !await rateLimiter.TryReserveAsync(AuthLimits.RegistrationMailHourly, recipient))
        {
            metrics.Increment("account_confirmation_mail_throttled");
            throw new TooManyRequestsException();
        }
    }

    private async Task<bool> ReserveRecoveryAttempt(long ownerId)
    {
        if (await rateLimiter.TryReserveAsync(AuthLimits.LoginByAccount, ownerId.ToString()))
            return true;

        metrics.Increment("auth_login_failed");
        metrics.Increment("auth_login_failed_locked");
        logger.LogWarning("Восстановление черновика заблокировано лимитом попыток для пользователя {UserId}", ownerId);
        return false;
    }

    private async Task OverrideDraftAsync(AddDraftUserRequest request, CancellationToken cancellationToken)
    {
        await usersClient.OverrideDraftUserAsync(request,
            deadline: DateTime.UtcNow.Add(UsersCallTimeout), cancellationToken: cancellationToken);
        metrics.Increment("accounts_draft_overridden");
    }

    private Task<ConfirmUserResponse> ConfirmUserAsync(long ownerId, CancellationToken cancellationToken) =>
        usersClient.ConfirmUserAsync(new ConfirmUserRequest { UserId = ownerId },
            deadline: DateTime.UtcNow.Add(UsersCallTimeout), cancellationToken: cancellationToken).ResponseAsync;

    private async Task<string> CreateRefreshToken(long ownerId, CancellationToken cancellationToken)
    {
        var refreshToken = RefreshTokenGenerator.GenerateRefreshToken();
        await refreshTokensStorage.CreateNewRefreshToken(refreshToken, ownerId,
            requestContext.DeviceId ?? requestContext.DeviceName!, ExpDaysRefreshToken, cancellationToken);
        return refreshToken;
    }

    private static CreateAccountResponse ConfirmedResponse(string refreshToken) => new()
    {
        RefreshToken = new Token
        {
            Value = refreshToken,
            ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(ExpDaysRefreshToken))
        }
    };

    private static void ValidateInitialPassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw new InvalidPasswordException();
    }
}

using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;
using BarkCloud.Web.Rendering;

using Google.Protobuf;

using Grpc.Core;

using System.Text.Json;

namespace BarkCloud.Web;

/// <summary>
/// Эндпоинты страницы настроек: профиль, приватность, безопасность (пароль/2FA),
/// устройства и сессии, удаление аккаунта и аватар. Каждый требует авторизации
/// (см. <see cref="AuthGateway"/>) и проксирует действие в соответствующий gRPC-сервис
/// с пользовательским токеном. Аватар идёт через серверные API (Files+Users) с сервисным токеном.
/// </summary>
public static class SettingsEndpoints
{
    public sealed record NameBody(string? FirstName, string? LastName);
    public sealed record BioBody(string? Bio);
    public sealed record UsernameBody(string? Username);
    public sealed record PrivacyBody(int ProfileVisibility, int EmailVisibility, int LastSeenVisibility, bool SearchableByUsername);
    public sealed record PasswordBody(string? OldPassword, string? NewPassword);
    public sealed record OtpEnableBody(int OtpType);
    public sealed record OtpConfirmBody(string? OtpCode);
    public sealed record OtpDisableBody(int OtpType, string? OtpCode);
    public sealed record RenameBody(string? DeviceId, string? CustomName);
    public sealed record RevokeBody(string? DeviceId);
    public sealed record WebAuthnRegisterCompleteBody(string? ChallengeId, JsonElement Attestation, string? Name);
    public sealed record WebAuthnRemoveBody(string? CredentialId);

    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/settings");

        // ───────── Полное состояние страницы настроек ─────────

        // Раньше собиралось серверно и инлайнилось в Settings.html (page_data_json).
        // Теперь SPA грузит его через /api/settings/full при монтировании страницы настроек.
        api.MapGet("/full", async (HttpContext http, AuthGateway auth, PageDataBuilder data) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null) return Results.Unauthorized();

            var json = await data.BuildSettingsJsonAsync(user, http);
            return Results.Content(json, "application/json; charset=utf-8");
        });

        // ───────── Профиль ─────────

        api.MapPost("/profile/name", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, NameBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await users.ChangeNameAsync(new ChangeNameRequest
                {
                    FirstName = body.FirstName ?? "",
                    LastName = body.LastName ?? ""
                }, token);
                return Results.Ok(new { ok = true });
            }));

        api.MapPost("/profile/bio", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, BioBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await users.ChangeBioAsync(new ChangeBioRequest { Bio = body.Bio ?? "" }, token);
                return Results.Ok(new { ok = true });
            }));

        api.MapGet("/profile/username-available", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, string? u) =>
            Do(http, auth, async (_, token) =>
            {
                var resp = await users.CheckExistUsernameAsync(new CheckExistUsernameRequest { Username = u ?? "" }, token);
                return Results.Ok(new { available = !resp.Exist });
            }));

        api.MapPost("/profile/username", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, UsernameBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await users.ChangeUsernameAsync(new ChangeUsernameRequest { Username = body.Username ?? "" }, token);
                return Results.Ok(new { ok = true });
            }));

        // ───────── Приватность ─────────

        api.MapGet("/privacy", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users) =>
            Do(http, auth, async (_, token) =>
            {
                var resp = await users.GetPrivacySettingsAsync(new GetPrivacySettingsRequest(), token);
                return Results.Ok(MapPrivacy(resp.Settings));
            }));

        api.MapPost("/privacy", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, PrivacyBody body) =>
            Do(http, auth, async (_, token) =>
            {
                var settings = new PrivacySettings
                {
                    ProfileVisibility = (PrivacyVisibility)body.ProfileVisibility,
                    EmailVisibility = (PrivacyVisibility)body.EmailVisibility,
                    LastSeenVisibility = (PrivacyVisibility)body.LastSeenVisibility,
                    SearchableByUsername = body.SearchableByUsername
                };
                var resp = await users.UpdatePrivacySettingsAsync(new UpdatePrivacySettingsRequest { Settings = settings }, token);
                return Results.Ok(MapPrivacy(resp.Settings));
            }));

        // ───────── Безопасность ─────────

        api.MapPost("/security/password", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, PasswordBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await identity.SetPasswordAsync(new SetPasswordRequest
                {
                    Password = body.NewPassword ?? "",
                    OldPassword = body.OldPassword ?? ""
                }, token);
                return Results.Ok(new { ok = true });
            }));

        api.MapGet("/security/2fa", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity) =>
            Do(http, auth, async (_, token) =>
            {
                var resp = await identity.ListOtpVerificationAsync(new ListOtpVerificationRequest(), token);
                return Results.Ok(new { authenticator = resp.AuthenticatorEnabled, email = resp.EmailEnabled });
            }));

        api.MapPost("/security/2fa/enable", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, OtpEnableBody body) =>
            Do(http, auth, async (_, token) =>
            {
                var resp = await identity.EnableOtpVerificationAsync(new EnableOtpVerificationRequest
                {
                    OtpType = (OtpTypeId)body.OtpType
                }, token);
                return Results.Ok(new { qr = resp.OtpQr, code = resp.OtpCode });
            }));

        api.MapPost("/security/2fa/confirm", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, OtpConfirmBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await identity.ConfirmOtpVerificationAsync(new ConfirmOtpVerificationRequest { OtpCode = body.OtpCode ?? "" }, token);
                return Results.Ok(new { ok = true });
            }));

        api.MapPost("/security/2fa/disable", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, OtpDisableBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await identity.DisableOtpVerificationAsync(new DisableOtpVerificationRequest
                {
                    OtpType = (OtpTypeId)body.OtpType,
                    OtpCode = body.OtpCode ?? ""
                }, token);
                return Results.Ok(new { ok = true });
            }));

        // ───────── Ключи безопасности (WebAuthn) ─────────

        api.MapGet("/security/webauthn", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity) =>
            Do(http, auth, async (_, token) =>
            {
                var resp = await identity.ListWebAuthnCredentialsAsync(new ListWebAuthnCredentialsRequest(), token);
                var keys = resp.Credentials.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    createdAt = c.CreatedAt?.ToDateTimeOffset(),
                    lastUsedAt = c.LastUsedAt?.ToDateTimeOffset()
                });
                return Results.Ok(new { keys });
            }));

        api.MapPost("/security/webauthn/register/begin", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity) =>
            Do(http, auth, async (_, token) =>
            {
                var resp = await identity.BeginWebAuthnRegistrationAsync(new BeginWebAuthnRegistrationRequest(), token);
                return Results.Ok(new { optionsJson = resp.OptionsJson, challengeId = resp.ChallengeId });
            }));

        api.MapPost("/security/webauthn/register/complete", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, WebAuthnRegisterCompleteBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await identity.CompleteWebAuthnRegistrationAsync(new CompleteWebAuthnRegistrationRequest
                {
                    ChallengeId = body.ChallengeId ?? "",
                    AttestationJson = body.Attestation.GetRawText(),
                    CredentialName = body.Name ?? ""
                }, token);
                return Results.Ok(new { ok = true });
            }));

        api.MapPost("/security/webauthn/remove", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, WebAuthnRemoveBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await identity.RemoveWebAuthnCredentialAsync(new RemoveWebAuthnCredentialRequest { CredentialId = body.CredentialId ?? "" }, token);
                return Results.Ok(new { ok = true });
            }));

        // ───────── Устройства и сессии ─────────

        api.MapGet("/sessions", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity) =>
            Do(http, auth, async (user, token) =>
            {
                var active = await identity.GetActiveSessionsAsync(new GetActiveSessionsRequest(), token);
                var sessions = active.Sessions.Select(s => BuildSession(s, user.DeviceId)).ToList();
                return Results.Ok(new { sessions });
            }));

        api.MapPost("/devices/rename", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users, RenameBody body) =>
            Do(http, auth, async (_, token) =>
            {
                await users.RenameDeviceAsync(new RenameDeviceRequest
                {
                    DeviceId = body.DeviceId ?? "",
                    CustomName = body.CustomName ?? ""
                }, token);
                return Results.Ok(new { ok = true });
            }));

        // Завершить сессию на устройстве. RemoveActiveSession сам удаляет устройство в Users —
        // отдельный DeleteDevice не нужен. Текущую сессию здесь завершать запрещено (см. «Выйти»).
        api.MapPost("/sessions/revoke", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity, RevokeBody body) =>
            Do(http, auth, async (user, token) =>
            {
                if (string.IsNullOrEmpty(body.DeviceId))
                    return Results.BadRequest(new { message = "Не указано устройство" });
                if (!string.IsNullOrEmpty(user.DeviceId) && body.DeviceId == user.DeviceId)
                    return Results.BadRequest(new { message = "Нельзя завершить текущую сессию здесь — используйте «Выйти»" });

                await identity.RemoveActiveSessionAsync(new RemoveActiveSessionRequest { DeviceId = body.DeviceId }, token);
                return Results.Ok(new { ok = true });
            }));

        api.MapPost("/sessions/revoke-others", (HttpContext http, AuthGateway auth, IdentityApi.IdentityApiClient identity) =>
            Do(http, auth, async (user, token) =>
            {
                var active = await identity.GetActiveSessionsAsync(new GetActiveSessionsRequest(), token);
                var revoked = 0;
                foreach (var s in active.Sessions)
                {
                    if (string.IsNullOrEmpty(s.DeviceId) || s.DeviceId == user.DeviceId) continue;
                    try
                    {
                        await identity.RemoveActiveSessionAsync(new RemoveActiveSessionRequest { DeviceId = s.DeviceId }, token);
                        revoked++;
                    }
                    catch (RpcException) { /* пропускаем уже отозванные */ }
                }
                return Results.Ok(new { revoked });
            }));

        // ───────── Аккаунт ─────────

        api.MapPost("/account/delete", (HttpContext http, AuthGateway auth, UsersApi.UsersApiClient users) =>
            Do(http, auth, async (_, token) =>
            {
                await users.DeleteAccountAsync(new DeleteAccountRequest(), token);
                auth.ClearSession(http); // сессии чистятся событием UserDeletedEvent — здесь только cookie
                return Results.Ok(new { ok = true });
            }));

        // ───────── Аватар (через серверные API) ─────────

        api.MapPost("/avatar", async (HttpContext http, AuthGateway auth,
            FilesServerApi.FilesServerApiClient files, UsersServerApi.UsersServerApiClient usersServer) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null) return Results.Unauthorized();
            if (!http.Request.HasFormContentType) return Results.BadRequest(new { message = "Ожидался multipart/form-data" });

            var form = await http.Request.ReadFormAsync();
            var file = form.Files["file"];
            if (file is null || file.Length == 0) return Results.BadRequest(new { message = "Файл не передан" });
            if (file.Length > 10 * 1024 * 1024) return Results.BadRequest(new { message = "Файл больше 10 МБ" });
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Допустимы только изображения" });

            try
            {
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    await file.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }

                var uploaded = await files.UploadAvatarServerAsync(new UploadAvatarServerRequest
                {
                    ImageData = ByteString.CopyFrom(bytes),
                    Filename = file.FileName,
                    UserId = user.UserId
                });

                await usersServer.SetProfilePictureServerAsync(new SetProfilePictureServerRequest
                {
                    UserId = user.UserId,
                    ProfilePictureUrl = uploaded.FileUrl,
                    ProfilePicturePreviewUrl = uploaded.PreviewUrl
                });

                return Results.Ok(new { avatarUrl = uploaded.FileUrl, avatarPreviewUrl = uploaded.PreviewUrl });
            }
            catch (RpcException ex) { return MapRpc(ex); }
        });

        api.MapPost("/avatar/remove", async (HttpContext http, AuthGateway auth, UsersServerApi.UsersServerApiClient usersServer) =>
        {
            var user = await auth.AuthenticateAsync(http);
            if (user is null) return Results.Unauthorized();
            try
            {
                await usersServer.SetProfilePictureServerAsync(new SetProfilePictureServerRequest
                {
                    UserId = user.UserId,
                    ProfilePictureUrl = "",
                    ProfilePicturePreviewUrl = ""
                });
                return Results.Ok(new { ok = true });
            }
            catch (RpcException ex) { return MapRpc(ex); }
        });
    }

    /// <summary>Авторизация + вызов gRPC c пользовательским токеном + единый маппинг ошибок.</summary>
    private static async Task<IResult> Do(HttpContext http, AuthGateway auth, Func<WebUser, Metadata, Task<IResult>> action)
    {
        var user = await auth.AuthenticateAsync(http);
        if (user is null) return Results.Unauthorized();

        try
        {
            return await action(user, BrowserContext.UserToken(user.AccessToken));
        }
        catch (RpcException ex)
        {
            return MapRpc(ex);
        }
    }

    private static IResult MapRpc(RpcException ex)
    {
        var detail = string.IsNullOrWhiteSpace(ex.Status.Detail) ? null : ex.Status.Detail;
        return ex.StatusCode switch
        {
            StatusCode.Unauthenticated => Results.Unauthorized(),
            StatusCode.AlreadyExists => Results.BadRequest(new { message = detail ?? "Уже занято" }),
            StatusCode.NotFound => Results.BadRequest(new { message = detail ?? "Не найдено" }),
            StatusCode.PermissionDenied => Results.BadRequest(new { message = detail ?? "Недостаточно прав" }),
            _ => Results.BadRequest(new { message = detail ?? "Ошибка сервиса" })
        };
    }

    private static object MapPrivacy(PrivacySettings s) => new
    {
        profileVisibility = (int)s.ProfileVisibility,
        emailVisibility = (int)s.EmailVisibility,
        lastSeenVisibility = (int)s.LastSeenVisibility,
        searchableByUsername = s.SearchableByUsername
    };

    private static object BuildSession(GetActiveSessionsResponse.Types.Session s, string? currentDeviceId)
    {
        var device = !string.IsNullOrEmpty(s.CustomName) ? s.CustomName : s.OriginalName;
        return new
        {
            deviceId = s.DeviceId,
            device = string.IsNullOrEmpty(device) ? s.AppName : device,
            os = s.OperationSystem,
            location = string.IsNullOrEmpty(s.Location) ? s.AppName : $"{s.Location} · {s.AppName}",
            when = Format.Relative(s.CreatedAt.ToDateTimeOffset()),
            current = !string.IsNullOrEmpty(currentDeviceId) && s.DeviceId == currentDeviceId
        };
    }
}

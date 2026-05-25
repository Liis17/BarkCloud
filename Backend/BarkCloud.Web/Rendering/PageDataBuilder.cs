using System.Text.Json;

using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;
using BarkCloud.Web.Auth;
using BarkCloud.Web.Infrastructure;

using Grpc.Core;

namespace BarkCloud.Web.Rendering;

/// <summary>
/// Собирает данные для страниц из микросервисов: переменные общего каркаса
/// (sidebar/topbar) и JSON-полезную нагрузку отдельных страниц.
/// Все обращения отказоустойчивы: при недоступности сервиса страница всё равно
/// отдаётся, а недостающие данные деградируют до пустых значений / demo-fallback.
/// </summary>
public sealed class PageDataBuilder
{
    private static readonly JsonSerializerOptions Json = new(); // дефолтный энкодер экранирует < > & — безопасно в <script>

    private readonly UsersApi.UsersApiClient _users;
    private readonly UsersServerApi.UsersServerApiClient _usersServer;
    private readonly FilesApi.FilesApiClient _files;
    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly AdminGate _admin;
    private readonly IConfiguration _config;
    private readonly ILogger<PageDataBuilder> _logger;

    public PageDataBuilder(
        UsersApi.UsersApiClient users,
        UsersServerApi.UsersServerApiClient usersServer,
        FilesApi.FilesApiClient files,
        IdentityApi.IdentityApiClient identity,
        AdminGate admin,
        IConfiguration config,
        ILogger<PageDataBuilder> logger)
    {
        _users = users;
        _usersServer = usersServer;
        _files = files;
        _identity = identity;
        _admin = admin;
        _config = config;
        _logger = logger;
    }

    // ───────────────────────── Общий каркас (shared.jsx) ─────────────────────────

    public async Task<Dictionary<string, string?>> BuildShellAsync(WebUser user, HttpContext http)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        var vars = new Dictionary<string, string?>
        {
            ["app.version"] = _config.Value("App:Version", "v1.0.0"),
            ["app.edition"] = _config.Value("App:Edition", "self-host"),
            ["server.host"] = _config.Value("App:PublicHost", http.Request.Host.Value),
            ["sync.status"] = "Синхронизировано",
            ["sync.last_at"] = Format.Time(DateTimeOffset.UtcNow),
            ["user.initials"] = "?",
            ["user.display_name"] = "Пользователь",
            ["user.role"] = "",
            ["user.avatar_url"] = "",
            ["storage.used_label"] = "0 Б",
            ["storage.total_label"] = "0 Б",
            ["storage.percent"] = "0",
            // навигационные счётчики опускаем — пустые значения скрывают бейджи
            ["nav.photos_count"] = "",
            ["nav.videos_count"] = "",
            ["nav.files_count"] = "",
            ["nav.shared_count"] = "",
            ["nav.links_count"] = ""
        };

        User? profile = null;

        try
        {
            var response = await _users.GetUserAsync(new GetUserRequest { UserId = user.UserId }, token);
            profile = response.User;

            var name = $"{profile.FirstName} {profile.LastName}".Trim();
            vars["user.display_name"] = string.IsNullOrEmpty(name) ? profile.Username : name;
            vars["user.role"] = profile.Username;
            vars["user.initials"] = Format.Initials(profile.FirstName, profile.LastName);
            vars["user.avatar_url"] = string.IsNullOrEmpty(profile.ProfilePicturePreview)
                ? profile.ProfilePicture ?? ""
                : profile.ProfilePicturePreview;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("GetUser не выполнен: {Status}", ex.StatusCode);
        }

        try
        {
            var storage = await _files.GetUserStorageInfoAsync(new GetUserStorageInfoRequest(), token);
            var limit = ResolveLimit(storage.StorageLimit, profile?.StorageLimitGb ?? 0);

            vars["storage.used_label"] = Format.Size(storage.TotalUsedStorage);
            vars["storage.total_label"] = Format.Size(limit);
            vars["storage.percent"] = Format.Percent(storage.TotalUsedStorage, limit).ToString();
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("GetUserStorageInfo не выполнен: {Status}", ex.StatusCode);
        }

        return vars;
    }

    // ───────────────────────── Settings ─────────────────────────

    public async Task<string> BuildSettingsJsonAsync(WebUser user, HttpContext http)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        User? profile = null;
        try { profile = (await _users.GetUserAsync(new GetUserRequest { UserId = user.UserId }, token)).User; }
        catch (RpcException ex) { _logger.LogWarning("Settings/GetUser: {Status}", ex.StatusCode); }

        // Email хранится в Users, но недоступен через клиентский GetUser — берём серверным API.
        var email = "";
        try { email = (await _usersServer.GetUserContactsAsync(new GetUserContactsRequest { UserId = user.UserId })).Contact?.Email ?? ""; }
        catch (RpcException ex) { _logger.LogWarning("Settings/GetUserContacts: {Status}", ex.StatusCode); }

        bool authenticator = false, emailOtp = false;
        try
        {
            var otp = await _identity.ListOtpVerificationAsync(new ListOtpVerificationRequest(), token);
            authenticator = otp.AuthenticatorEnabled;
            emailOtp = otp.EmailEnabled;
        }
        catch (RpcException ex) { _logger.LogWarning("Settings/ListOtp: {Status}", ex.StatusCode); }

        object privacy = new { profileVisibility = 0, emailVisibility = 0, lastSeenVisibility = 0, searchableByUsername = true };
        try
        {
            var p = (await _users.GetPrivacySettingsAsync(new GetPrivacySettingsRequest(), token)).Settings;
            privacy = new
            {
                profileVisibility = (int)p.ProfileVisibility,
                emailVisibility = (int)p.EmailVisibility,
                lastSeenVisibility = (int)p.LastSeenVisibility,
                searchableByUsername = p.SearchableByUsername
            };
        }
        catch (RpcException ex) { _logger.LogWarning("Settings/GetPrivacy: {Status}", ex.StatusCode); }

        var sessions = new List<object>();
        try
        {
            var active = await _identity.GetActiveSessionsAsync(new GetActiveSessionsRequest(), token);
            foreach (var s in active.Sessions)
            {
                var device = !string.IsNullOrEmpty(s.CustomName) ? s.CustomName : s.OriginalName;
                sessions.Add(new
                {
                    deviceId = s.DeviceId,
                    device = string.IsNullOrEmpty(device) ? s.AppName : device,
                    os = s.OperationSystem,
                    location = string.IsNullOrEmpty(s.Location) ? s.AppName : $"{s.Location} · {s.AppName}",
                    when = Format.Relative(s.CreatedAt.ToDateTimeOffset()),
                    current = !string.IsNullOrEmpty(user.DeviceId) && s.DeviceId == user.DeviceId
                });
            }
        }
        catch (RpcException ex) { _logger.LogWarning("Settings/GetActiveSessions: {Status}", ex.StatusCode); }

        var devicesCount = 0;
        try { devicesCount = (await _users.GetDevicesAsync(new GetDevicesRequest(), token)).Devices.Count; }
        catch (RpcException ex) { _logger.LogWarning("Settings/GetDevices: {Status}", ex.StatusCode); }

        var (storageBlock, _) = await BuildStorageAsync(token, profile, devicesCount);

        var displayName = $"{profile?.FirstName} {profile?.LastName}".Trim();

        var payload = new
        {
            profile = new
            {
                initials = profile is null ? "?" : Format.Initials(profile.FirstName, profile.LastName),
                firstName = profile?.FirstName ?? "",
                lastName = profile?.LastName ?? "",
                name = string.IsNullOrEmpty(displayName) ? profile?.Username ?? "" : displayName,
                email,
                username = profile?.Username ?? "",
                bio = profile?.Bio ?? "",
                avatarUrl = profile?.ProfilePicture ?? "",
                avatarPreviewUrl = profile?.ProfilePicturePreview ?? ""
            },
            security = new
            {
                twoFa = authenticator || emailOtp,
                authenticator,
                emailOtp
            },
            privacy,
            storage = storageBlock,
            sessions,
            sessionsHeader = $"{sessions.Count} {Plural(sessions.Count, "устройство", "устройства", "устройств")} с активным доступом",
            admin = new
            {
                enabled = _admin.Enabled,
                unlocked = _admin.IsUnlocked(http)
            },
            system = new
            {
                version = _config.Value("App:Version", "v1.0.0"),
                edition = _config.Value("App:Edition", "self-host")
            }
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private async Task<(object Block, long Limit)> BuildStorageAsync(Metadata token, User? profile, int devicesCount)
    {
        long used = 0, limit = ResolveLimit(0, profile?.StorageLimitGb ?? 0);
        var breakdown = new List<object>();

        try
        {
            var storage = await _files.GetUserStorageInfoAsync(new GetUserStorageInfoRequest(), token);
            used = storage.TotalUsedStorage;
            limit = ResolveLimit(storage.StorageLimit, profile?.StorageLimitGb ?? 0);

            foreach (var byType in storage.StorageByTypes)
            {
                if (byType.UsedStorage <= 0) continue;

                var (label, color) = byType.FileType switch
                {
                    UploadFileType.UserAvatar => ("Аватары", "#7B5DA8"),
                    UploadFileType.CloudFile => ("Файлы", "#9A4F1E"),
                    _ => ("Прочее", "var(--md-outline)")
                };

                breakdown.Add(new
                {
                    k = label,
                    v = Format.Size(byType.UsedStorage),
                    color,
                    pct = Format.Percent(byType.UsedStorage, limit)
                });
            }
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Settings/Storage: {Status}", ex.StatusCode);
        }

        var block = new
        {
            used = Format.ToGb(used),
            total = Format.ToGb(limit),
            unit = "ГБ",
            percent = Format.Percent(used, limit),
            forecast = "—",
            breakdown,
            freeLabel = Format.Size(Math.Max(0, limit - used)),
            autoUpload = true,
            devicesCount = $"{devicesCount} {Plural(devicesCount, "устройство", "устройства", "устройств")}",
            trashLabel = "—"
        };

        return (block, limit);
    }

    private static long ResolveLimit(long storageLimit, int limitGb)
    {
        if (storageLimit > 0) return storageLimit;
        if (limitGb > 0) return limitGb * 1024L * 1024L * 1024L;
        return 0;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return one;
        if (mod10 is >= 2 and <= 4 && mod100 is < 10 or >= 20) return few;
        return many;
    }
}

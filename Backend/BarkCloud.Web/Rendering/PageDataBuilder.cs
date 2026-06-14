using System.Globalization;
using System.Text.Json;

using BarkCloud.GrpcServer;
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

    // ───────────────────────── Общий каркас (GET /api/me) ─────────────────────────

    public async Task<Dictionary<string, string?>> BuildShellAsync(WebUser user, HttpContext http)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        var vars = new Dictionary<string, string?>
        {
            ["app.version"] = _config.Value("App:Version", AppVersion.Current),
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
            ["storage.other_pct"] = "0",
            ["storage.s3_pct"] = "0",
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

            var diskTotal = storage.TotalAvailableStorage;
            var diskUsed = storage.DiskUsedStorage + storage.S3UsedStorage;

            vars["storage.used_label"] = Format.Size(diskUsed);
            vars["storage.total_label"] = Format.Size(diskTotal);
            vars["storage.percent"] = Format.Percent(diskUsed, diskTotal).ToString();
            vars["storage.other_pct"] = PctOf(storage.DiskUsedStorage, diskTotal).ToString(CultureInfo.InvariantCulture);
            vars["storage.s3_pct"] = PctOf(storage.S3UsedStorage, diskTotal).ToString(CultureInfo.InvariantCulture);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("GetUserStorageInfo не выполнен: {Status}", ex.StatusCode);
        }

        return vars;
    }

    /// <summary>
    /// Только блок хранилища для сайдбара (GET /api/storage) — без профиля.
    /// Показывает заполнение физического диска сервера (не-S3 + S3), как и вкладка настроек.
    /// </summary>
    public async Task<object> BuildStorageAsync(WebUser user)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        long diskTotal = 0, diskOther = 0, diskS3 = 0;
        try
        {
            var storage = await _files.GetUserStorageInfoAsync(new GetUserStorageInfoRequest(), token);
            diskTotal = storage.TotalAvailableStorage;
            diskOther = storage.DiskUsedStorage;
            diskS3 = storage.S3UsedStorage;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Storage/GetUserStorageInfo не выполнен: {Status}", ex.StatusCode);
        }

        var diskUsed = diskOther + diskS3;

        return new
        {
            usedLabel = Format.Size(diskUsed),
            totalLabel = Format.Size(diskTotal),
            percent = Format.Percent(diskUsed, diskTotal),
            otherPct = PctOf(diskOther, diskTotal),
            s3Pct = PctOf(diskS3, diskTotal)
        };
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
                version = _config.Value("App:Version", AppVersion.Current),
                edition = _config.Value("App:Edition", "self-host"),
                emailEnabled = _config.EmailEnabled()
            }
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    // ───────────────────────── Helpers ─────────────────────────

    private async Task<(object Block, long Limit)> BuildStorageAsync(Metadata token, User? profile, int devicesCount)
    {
        long used = 0, limit = ResolveLimit(0, profile?.StorageLimitGb ?? 0);
        long diskTotal = 0, diskOther = 0, diskS3 = 0;
        var breakdown = new List<object>();

        try
        {
            var storage = await _files.GetUserStorageInfoAsync(new GetUserStorageInfoRequest(), token);
            used = storage.TotalUsedStorage;
            limit = ResolveLimit(storage.StorageLimit, profile?.StorageLimitGb ?? 0);
            diskTotal = storage.TotalAvailableStorage;
            diskOther = storage.DiskUsedStorage;
            diskS3 = storage.S3UsedStorage;

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

        var diskUsed = diskOther + diskS3;
        var diskFree = Math.Max(0, diskTotal - diskUsed);

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
            trashLabel = "—",
            disk = new
            {
                totalLabel = Format.Size(diskTotal),
                usedLabel = Format.Size(diskUsed),
                otherLabel = Format.Size(diskOther),
                s3Label = Format.Size(diskS3),
                freeLabel = Format.Size(diskFree),
                usedPct = Format.Percent(diskUsed, diskTotal),
                otherPct = PctOf(diskOther, diskTotal),
                s3Pct = PctOf(diskS3, diskTotal)
            }
        };

        return (block, limit);
    }

    // Доля в процентах с одним знаком после запятой — для плавной ширины сегментов бара.
    private static double PctOf(long part, long whole)
        => whole <= 0 ? 0 : Math.Round(Math.Clamp(part * 100d / whole, 0, 100), 1);

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

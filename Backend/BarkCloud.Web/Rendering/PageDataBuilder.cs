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

    private static readonly string[][] Tints =
    [
        ["#A8C99A", "#4A6F4A"], ["#B4A3D6", "#5B4889"], ["#E8A87C", "#7A4A2E"],
        ["#E0BB6F", "#8A5E2A"], ["#8FAFCC", "#3F5A7E"], ["#D696B0", "#6F3552"],
        ["#8FC3BB", "#2F5C56"], ["#C8A78C", "#6F4A3A"], ["#7E9FC3", "#2A436A"]
    ];

    private readonly UsersApi.UsersApiClient _users;
    private readonly FilesApi.FilesApiClient _files;
    private readonly CloudApi.CloudApiClient _cloud;
    private readonly IdentityApi.IdentityApiClient _identity;
    private readonly IConfiguration _config;
    private readonly ILogger<PageDataBuilder> _logger;

    public PageDataBuilder(
        UsersApi.UsersApiClient users,
        FilesApi.FilesApiClient files,
        CloudApi.CloudApiClient cloud,
        IdentityApi.IdentityApiClient identity,
        IConfiguration config,
        ILogger<PageDataBuilder> logger)
    {
        _users = users;
        _files = files;
        _cloud = cloud;
        _identity = identity;
        _config = config;
        _logger = logger;
    }

    // ───────────────────────── Общий каркас (shared.jsx) ─────────────────────────

    public async Task<Dictionary<string, string?>> BuildShellAsync(WebUser user, HttpContext http)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        var vars = new Dictionary<string, string?>
        {
            ["app.version"] = _config["App:Version"] ?? "v1.0.0",
            ["app.edition"] = _config["App:Edition"] ?? "self-host",
            ["server.host"] = _config["App:PublicHost"] ?? http.Request.Host.Value,
            ["sync.status"] = "Синхронизировано",
            ["sync.last_at"] = Format.Time(DateTimeOffset.UtcNow),
            ["user.initials"] = "?",
            ["user.display_name"] = "Пользователь",
            ["user.role"] = "",
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

        var twoFa = false;
        try
        {
            var otp = await _identity.ListOtpVerificationAsync(new ListOtpVerificationRequest(), token);
            twoFa = otp.AuthenticatorEnabled || otp.EmailEnabled;
        }
        catch (RpcException ex) { _logger.LogWarning("Settings/ListOtp: {Status}", ex.StatusCode); }

        var sessions = new List<object>();
        try
        {
            var active = await _identity.GetActiveSessionsAsync(new GetActiveSessionsRequest(), token);
            foreach (var s in active.Sessions)
            {
                var device = !string.IsNullOrEmpty(s.CustomName) ? s.CustomName : s.OriginalName;
                sessions.Add(new
                {
                    device = string.IsNullOrEmpty(device) ? s.AppName : device,
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
                name = string.IsNullOrEmpty(displayName) ? profile?.Username ?? "" : displayName,
                email = "",
                username = profile?.Username ?? "",
                timezone = ""
            },
            security = new
            {
                passwordChanged = "—",
                passwordStrength = "Задан",
                twoFa,
                e2e = true,
                backupCodes = "—"
            },
            storage = storageBlock,
            sessions,
            sessionsHeader = $"{sessions.Count} {Plural(sessions.Count, "устройство", "устройства", "устройств")} с активным доступом"
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    // ───────────────────────── Files ─────────────────────────

    public async Task<string> BuildFilesJsonAsync(WebUser user)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        DirectoryListingDetailed listing;
        try
        {
            listing = await _cloud.ListDirectoryDetailedAsync(new ListDirectoryRequest(), token);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Files/ListDirectoryDetailed: {Status}", ex.StatusCode);
            return string.Empty; // demo-fallback на странице
        }

        var files = new List<object>();

        foreach (var dir in listing.Subdirs)
        {
            files.Add(new
            {
                id = dir.Id,
                kind = "folder",
                ext = "DIR",
                name = dir.Name,
                meta = "",
                size = "",
                mod = Format.Date(dir.UpdatedAt.ToDateTimeOffset()),
                ago = Format.Relative(dir.UpdatedAt.ToDateTimeOffset()),
                owner = "Я",
                tone = "p",
                shared = "—",
                star = false
            });
        }

        foreach (var item in listing.Files)
        {
            var (kind, ext) = FileKind.Classify(item.Entry.Name);
            var uploaded = item.File.UploadedAt.ToDateTimeOffset();

            files.Add(new
            {
                id = item.Entry.Id,
                kind,
                ext,
                name = item.Entry.Name,
                meta = "",
                size = Format.Size(item.File.FileSize),
                mod = Format.Date(uploaded),
                ago = Format.Relative(uploaded),
                owner = "Я",
                tone = "p",
                shared = "—",
                star = false
            });
        }

        var selectedId = listing.Subdirs.Count > 0
            ? listing.Subdirs[0].Id
            : listing.Files.Count > 0 ? listing.Files[0].Entry.Id : "";

        var payload = new
        {
            tabs = new object[]
            {
                new { key = "all", label = "Всё", count = files.Count },
                new { key = "recent", label = "Недавнее" },
                new { key = "starred", label = "Избранное" },
                new { key = "shared", label = "Общие" },
                new { key = "trash", label = "Корзина" }
            },
            breadcrumb = Array.Empty<object>(),
            files,
            selectedId
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    // ───────────────────────── Photos ─────────────────────────

    public async Task<string> BuildPhotosJsonAsync(WebUser user)
    {
        var token = BrowserContext.UserToken(user.AccessToken);

        ListUserImagesResponse images;
        try
        {
            images = await _cloud.ListUserImagesAsync(new ListUserImagesRequest { Limit = 60 }, token);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Photos/ListUserImages: {Status}", ex.StatusCode);
            return string.Empty; // demo-fallback на странице
        }

        var today = DateTimeOffset.UtcNow.ToLocalTime().Date;
        var groups = images.Items
            .Where(i => i.File is not null)
            .GroupBy(i => i.File.UploadedAt.ToDateTimeOffset().ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select((g, gi) => new
            {
                label = g.Key == today ? $"Сегодня, {Format.Date(g.First().File.UploadedAt.ToDateTimeOffset())}"
                    : g.Key == today.AddDays(-1) ? "Вчера"
                    : Format.Date(g.First().File.UploadedAt.ToDateTimeOffset()),
                meta = $"{g.Count()} {Plural(g.Count(), "фото", "фото", "фото")}",
                photos = g.Select((item, pi) => new
                {
                    tint = Tints[(gi + pi) % Tints.Length],
                    url = PreviewUrl(item.File),
                    fav = false
                }).Cast<object>().ToArray()
            })
            .Cast<object>()
            .ToArray();

        var payload = new
        {
            filters = new object[] { new { key = "all", label = "Все фото", count = images.Items.Count } },
            groups,
            memoriesUpdated = Format.Time(DateTimeOffset.UtcNow)
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
            used = ToGb(used),
            total = ToGb(limit),
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

    private static string PreviewUrl(UploadFileInfo file)
    {
        if (file.Previews.Count > 0)
        {
            // ближайшее к 512px превью
            var best = file.Previews
                .OrderBy(p => Math.Abs(p.TargetWidth - 512))
                .First();
            if (!string.IsNullOrEmpty(best.PreviewUrl))
                return best.PreviewUrl;
        }

        return file.FileUrl;
    }

    private static long ResolveLimit(long storageLimit, int limitGb)
    {
        if (storageLimit > 0) return storageLimit;
        if (limitGb > 0) return limitGb * 1024L * 1024L * 1024L;
        return 0;
    }

    private static string ToGb(long bytes)
        => (bytes / (1024d * 1024d * 1024d)).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    private static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (mod10 == 1 && mod100 != 11) return one;
        if (mod10 is >= 2 and <= 4 && mod100 is < 10 or >= 20) return few;
        return many;
    }
}

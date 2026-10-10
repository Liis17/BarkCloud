using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Contracts.Localization;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

// Реализация IPC-контракта: оркестрирует логин/refresh (TokenManager),
// монтирование (MountManager) и отдаёт состояние.
public sealed class DriveEngine : IDriveEngine
{
    private readonly TokenManager _tokens;
    private readonly CloudGateway _gateway;
    private readonly MountManager _mount;
    private readonly BarkCloudFileSystem _fs;
    private readonly CancellationTokenSource _lifetime;
    private readonly EngineSettingsStore _settings;
    private readonly UserProfile _profile;
    private readonly UploadOutboxWorker _outboxWorker;
    private readonly string _serverKey;
    private readonly string _serverHost;
    private string? _outboxOwnerUserId;

    internal DriveEngine(TokenManager tokens, CloudGateway gateway, MountManager mount,
        BarkCloudFileSystem fs, CancellationTokenSource lifetime, EngineSettingsStore settings,
        UserProfile profile, UploadOutboxWorker outboxWorker, string serverKey, string serverHost)
    {
        _tokens = tokens;
        _gateway = gateway;
        _mount = mount;
        _fs = fs;
        _lifetime = lifetime;
        _settings = settings;
        _profile = profile;
        _outboxWorker = outboxWorker;
        _serverKey = serverKey;
        _serverHost = serverHost;
    }

    public async Task<EngineStatus> LoginAsync(string login, string password, string? otpCode)
    {
        // Пустой логин → сервер кинул бы FailedPrecondition «Не передан ни логин ни email».
        // Отсекаем здесь: при восстановленной из refresh.bin сессии вход вообще не нужен.
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return ErrorMessage(Loc.T("Eng_LoginRequired"));

        try
        {
            await _tokens.LoginAsync(login, password, otpCode);
            _settings.ClearLastUserId(_serverKey);
            _profile.Clear();
            _outboxOwnerUserId = null;
            _fs.UpdateOutboxSession(null);
            await _profile.EnsureLoadedAsync();
            await StartOutboxForCurrentSessionAsync(forceTrigger: true);
            return Status(Loc.T("Eng_AuthSuccess"));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public async Task<WebAuthnChallenge> BeginWebAuthnAsync()
    {
        try
        {
            var (optionsJson, challengeId) = await _tokens.BeginWebAuthnAsync();
            return new WebAuthnChallenge
            {
                OptionsJson = optionsJson,
                ChallengeId = challengeId,
                RpId = ExtractRpId(optionsJson)
            };
        }
        catch (Exception ex)
        {
            // Нет ключей / ошибка — пустой ChallengeId сигнализирует UI о недоступности.
            EngineLog.Error("BeginWebAuthn", ex);
            return new WebAuthnChallenge();
        }
    }

    public async Task<EngineStatus> CompleteWebAuthnAsync(string challengeId, string assertionJson)
    {
        try
        {
            await _tokens.CompleteWebAuthnAsync(challengeId, assertionJson);
            _settings.ClearLastUserId(_serverKey);
            _profile.Clear();
            _outboxOwnerUserId = null;
            _fs.UpdateOutboxSession(null);
            await _profile.EnsureLoadedAsync();
            await StartOutboxForCurrentSessionAsync(forceTrigger: true);
            return Status(Loc.T("Eng_AuthSuccess"));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    private static string ExtractRpId(string optionsJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(optionsJson);
            return doc.RootElement.TryGetProperty("rpId", out var rp) ? rp.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<EngineStatus> LogoutAsync()
    {
        try
        {
            await _outboxWorker.StopAsync();
            _gateway.FlushPending(); // дослать буферизованные удаления, пока токен ещё валиден
            _mount.Unmount();
            _tokens.Logout();
            _settings.ClearLastUserId(_serverKey);
            _profile.Clear();
            _outboxOwnerUserId = null;
            _fs.UpdateOutboxSession(null);
            return Status(Loc.T("Eng_LoggedOut"));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public Task<EngineStatus> MountAsync(string driveLetter, string? volumeLabel)
    {
        try
        {
            if (!_tokens.IsAuthenticated)
                return Task.FromResult(ErrorMessage(Loc.T("Eng_LoginFirst")));

            if (_mount.IsMounted)
                return Task.FromResult(Status(Loc.T("Eng_AlreadyMounted"))); // идемпотентность (гонка авто-монтажа движка и UI)

            if (!string.IsNullOrWhiteSpace(volumeLabel))
                _fs.VolumeLabel = volumeLabel.Trim();

            _mount.Mount(driveLetter, _fs);
            _settings.SetLastMount(driveLetter, _fs.VolumeLabel);
            return Task.FromResult(Status(Loc.T("Eng_MountedFmt", driveLetter)));
        }
        catch (Exception ex) when (IsDokanMissing(ex))
        {
            return Task.FromResult(ErrorMessage(Loc.T("Eng_DokanMissing")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
        }
    }

    // Перемонтирование с новой буквой/меткой (null = текущее). Используется для
    // переименования диска и смены буквы — Dokany читает их только при маунте.
    public Task<EngineStatus> RemountAsync(string? driveLetter, string? volumeLabel)
    {
        try
        {
            if (!_tokens.IsAuthenticated)
                return Task.FromResult(ErrorMessage(Loc.T("Eng_LoginFirst")));

            var letter = (string.IsNullOrWhiteSpace(driveLetter) ? _mount.DriveLetter : driveLetter.Trim())
                ?? throw new InvalidOperationException(Loc.T("Eng_NoDriveLetter"));

            if (!string.IsNullOrWhiteSpace(volumeLabel))
                _fs.VolumeLabel = volumeLabel.Trim();

            if (_mount.IsMounted)
                _mount.Unmount();

            _mount.Mount(letter, _fs);
            _settings.SetLastMount(letter, _fs.VolumeLabel);
            return Task.FromResult(Status(Loc.T("Eng_RemountedFmt", letter, _fs.VolumeLabel)));
        }
        catch (Exception ex) when (IsDokanMissing(ex))
        {
            return Task.FromResult(ErrorMessage(Loc.T("Eng_DokanMissingShort")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
        }
    }

    // dokan2.dll отсутствует, если не установлен драйвер Dokany.
    private static bool IsDokanMissing(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is DllNotFoundException || (e.Message?.Contains("dokan2.dll", StringComparison.OrdinalIgnoreCase) ?? false))
                return true;
        return false;
    }

    public Task<EngineStatus> UnmountAsync()
    {
        try
        {
            _gateway.FlushPending(); // дослать буферизованные удаления до размонтирования
            _mount.Unmount();
            return Task.FromResult(Status(Loc.T("Eng_Unmounted")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
        }
    }

    public async Task<EngineStatus> GetStatusAsync()
    {
        if (_tokens.IsAuthenticated)
        {
            await _profile.EnsureLoadedAsync();
            await StartOutboxForCurrentSessionAsync();
        }
        return Status(null);
    }

    public async Task<byte[]?> GetAvatarAsync()
    {
        await _profile.EnsureLoadedAsync();
        var url = _profile.AvatarUrl;
        return string.IsNullOrEmpty(url) ? null : await _gateway.DownloadAvatarAsync(url);
    }

    public Task<EngineSettings> GetSettingsAsync()
        => Task.FromResult(new EngineSettings { CacheDir = _settings.GetCacheDir() });

    public Task<EngineSettings> SetCacheDirAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException(Loc.T("Eng_EmptyCachePath"));

        _settings.SetCacheDir(path);
        _gateway.SetCacheDir(path);
        EngineLog.Info($"Папка кэша изменена: {path}");
        return Task.FromResult(new EngineSettings { CacheDir = path });
    }

    public Task SetLanguageAsync(string culture)
    {
        // Последующие Status()/ErrorMessage()/LastSyncError формируются на этом языке.
        Loc.SetCulture(culture);
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        await _outboxWorker.StopAsync();
        _gateway.FlushPending(); // дослать буферизованные удаления до завершения процесса
        _mount.Unmount();
        _lifetime.Cancel();
    }

    private Task StartOutboxForCurrentSessionAsync(bool forceTrigger = false)
    {
        _outboxWorker.Start();
        var userId = _profile.UserId;
        if (!string.IsNullOrEmpty(userId)
            && (forceTrigger || !string.Equals(_outboxOwnerUserId, userId, StringComparison.Ordinal)))
        {
            _fs.AdoptOutboxForSession(userId);
            _outboxOwnerUserId = userId;
            _outboxWorker.Trigger();
        }
        return Task.CompletedTask;
    }

    private EngineStatus Status(string? message)
    {
        var status = new EngineStatus
        {
            Authenticated = _tokens.IsAuthenticated,
            Mounted = _mount.IsMounted,
            DriveLetter = _mount.DriveLetter,
            Message = message,
            Username = _profile.Username,
            ServerHost = _serverHost,
            VolumeLabel = _fs.VolumeLabel,
            LastSyncError = _fs.LastSyncError,
            PendingUploads = _fs.PendingUploadCount,
        };

        if (_tokens.IsAuthenticated)
        {
            try
            {
                var storage = _gateway.GetStorage();
                status.DiskTotalBytes = storage.TotalAvailableStorage;
                status.DiskOtherBytes = storage.DiskUsedStorage;
                status.DiskS3Bytes = storage.S3UsedStorage;
            }
            catch
            {
                // квота недоступна — не критично для статуса
            }
        }

        return status;
    }

    private EngineStatus Error(Exception ex)
    {
        EngineLog.Error("DriveEngine", ex);
        var status = Status(null);
        status.Error = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
        return status;
    }

    private EngineStatus ErrorMessage(string message)
    {
        var status = Status(null);
        status.Error = message;
        return status;
    }
}

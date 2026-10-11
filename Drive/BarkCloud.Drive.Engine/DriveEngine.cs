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
    private readonly BarkCloudFileSystem _fs;
    private readonly CancellationTokenSource _lifetime;
    private readonly EngineSettingsStore _settings;
    private readonly UserProfile _profile;
    private readonly UploadOutboxWorker _outboxWorker;
    private readonly Action _unmount;
    private readonly Action<string> _mountDrive;
    private readonly Func<string?> _mountedDriveLetter;
    private readonly string _serverKey;
    private readonly string _serverHost;
    private readonly SemaphoreSlim _sessionOperations = new(1, 1);
    private readonly object _outboxStateLock = new();
    private string? _outboxOwnerUserId;
    private bool _sessionUnavailable;

    internal DriveEngine(TokenManager tokens, CloudGateway gateway, MountManager mount,
        BarkCloudFileSystem fs, CancellationTokenSource lifetime, EngineSettingsStore settings,
        UserProfile profile, UploadOutboxWorker outboxWorker, string serverKey, string serverHost,
        Action? unmount = null, Action<string>? mountDrive = null, Func<string?>? mountedDriveLetter = null)
    {
        _tokens = tokens;
        _gateway = gateway;
        _fs = fs;
        _lifetime = lifetime;
        _settings = settings;
        _profile = profile;
        _outboxWorker = outboxWorker;
        _unmount = unmount ?? mount.Unmount;
        _mountDrive = mountDrive ?? (letter => mount.Mount(letter, fs));
        _mountedDriveLetter = mountedDriveLetter ?? (() => mount.IsMounted ? mount.DriveLetter : null);
        _serverKey = serverKey;
        _serverHost = serverHost;
    }

    public Task<EngineStatus> LoginAsync(string login, string password, string? otpCode)
    {
        // При восстановленной сессии пустой логин не означает новый вход.
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(ErrorMessage(Loc.T("Eng_LoginRequired")));

        return SwitchSessionAsync(() => _tokens.LoginAsync(login, password, otpCode));
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

    public Task<EngineStatus> CompleteWebAuthnAsync(string challengeId, string assertionJson)
        => SwitchSessionAsync(() => _tokens.CompleteWebAuthnAsync(challengeId, assertionJson));

    private async Task<EngineStatus> SwitchSessionAsync(Func<Task<bool>> authenticate)
    {
        await _sessionOperations.WaitAsync();
        var previousUserId = _profile.UserId ?? _settings.GetLastUserId(_serverKey);
        var mountedLetter = _mountedDriveLetter();
        Exception? error = null;
        var applied = false;
        try
        {
            SetSessionUnavailable(true);
            await _outboxWorker.StopAsync();
            if (_tokens.IsAuthenticated)
                _gateway.FlushPending(TimeSpan.FromSeconds(2));
            if (mountedLetter is not null)
                _unmount(); // Cleanup/Persist используют токен и владельца прежнего аккаунта.
            if (_tokens.IsAuthenticated)
                _gateway.FlushPending(TimeSpan.FromSeconds(1));
            else
                _gateway.DropPendingDeletes();

            applied = await authenticate();
            if (applied)
            {
                _gateway.ClearCache();
                _profile.Clear(() => _settings.ClearLastUserId(_serverKey));
                lock (_outboxStateLock)
                    _outboxOwnerUserId = null;
                _fs.UpdateOutboxSession(null);
                await _profile.EnsureLoadedAsync();
                if (previousUserId is null
                    || !string.Equals(previousUserId, _profile.UserId, StringComparison.Ordinal))
                    _fs.RemovePlaceholdersForAccountChange();
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            try
            {
                SetSessionUnavailable(false);
                if (_tokens.IsAuthenticated)
                    await StartOutboxForCurrentSessionAsync(forceTrigger: true);
                if (mountedLetter is not null && _mountedDriveLetter() is null)
                    _mountDrive(mountedLetter);
            }
            catch (Exception ex)
            {
                EngineLog.Error("Восстановление диска после входа", ex);
                error ??= ex;
            }
            finally
            {
                _sessionOperations.Release();
            }
        }

        if (error is not null)
            return Error(error);
        return applied ? Status(Loc.T("Eng_AuthSuccess")) : ErrorMessage(Loc.T("Eng_AuthSuperseded"));
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
        await _sessionOperations.WaitAsync();
        lock (_outboxStateLock)
            _sessionUnavailable = true;

        try
        {
            _tokens.BeginLogout();
            await _outboxWorker.StopAsync();
            _gateway.FlushPending(TimeSpan.FromSeconds(2));
            _unmount();
            _gateway.FlushPending(TimeSpan.FromSeconds(1));
            _fs.RemovePlaceholdersForAccountChange();
            try { await _tokens.LogoutRemoteAsync(); }
            catch (Exception ex) { EngineLog.Error("Выход из Identity", ex); }
            _tokens.Logout();
            _gateway.ClearCache();
            _profile.Clear(() => _settings.ClearLastUserId(_serverKey));
            lock (_outboxStateLock)
                _outboxOwnerUserId = null;
            _fs.UpdateOutboxSession(null);
            return Status(Loc.T("Eng_LoggedOut"));
        }
        catch (Exception ex)
        {
            _tokens.ResumeRefreshLoop();
            lock (_outboxStateLock)
                _sessionUnavailable = false;
            await StartOutboxForCurrentSessionAsync();
            return Error(ex, includeStorage: false);
        }
        finally
        {
            _sessionOperations.Release();
        }
    }

    public async Task<EngineStatus> MountAsync(string driveLetter, string? volumeLabel)
    {
        await _sessionOperations.WaitAsync();
        try
        {
            if (IsSessionUnavailable() || !_tokens.IsAuthenticated)
                return ErrorMessage(Loc.T("Eng_LoginFirst"));

            if (_mountedDriveLetter() is not null)
                return Status(Loc.T("Eng_AlreadyMounted")); // идемпотентность (гонка авто-монтажа движка и UI)

            if (!string.IsNullOrWhiteSpace(volumeLabel))
                _fs.VolumeLabel = volumeLabel.Trim();

            _mountDrive(driveLetter);
            _settings.SetLastMount(driveLetter, _fs.VolumeLabel);
            return Status(Loc.T("Eng_MountedFmt", driveLetter));
        }
        catch (Exception ex) when (IsDokanMissing(ex))
        {
            return ErrorMessage(Loc.T("Eng_DokanMissing"));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
        finally
        {
            _sessionOperations.Release();
        }
    }

    // Перемонтирование с новой буквой/меткой (null = текущее). Используется для
    // переименования диска и смены буквы — Dokany читает их только при маунте.
    public async Task<EngineStatus> RemountAsync(string? driveLetter, string? volumeLabel)
    {
        await _sessionOperations.WaitAsync();
        try
        {
            if (IsSessionUnavailable() || !_tokens.IsAuthenticated)
                return ErrorMessage(Loc.T("Eng_LoginFirst"));

            var letter = (string.IsNullOrWhiteSpace(driveLetter) ? _mountedDriveLetter() : driveLetter.Trim())
                ?? throw new InvalidOperationException(Loc.T("Eng_NoDriveLetter"));

            if (!string.IsNullOrWhiteSpace(volumeLabel))
                _fs.VolumeLabel = volumeLabel.Trim();

            if (_mountedDriveLetter() is not null)
                _unmount();

            _mountDrive(letter);
            _settings.SetLastMount(letter, _fs.VolumeLabel);
            return Status(Loc.T("Eng_RemountedFmt", letter, _fs.VolumeLabel));
        }
        catch (Exception ex) when (IsDokanMissing(ex))
        {
            return ErrorMessage(Loc.T("Eng_DokanMissingShort"));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
        finally
        {
            _sessionOperations.Release();
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

    public async Task<EngineStatus> UnmountAsync()
    {
        await _sessionOperations.WaitAsync();
        try
        {
            _gateway.FlushPending(); // дослать буферизованные удаления до размонтирования
            _unmount();
            return Status(Loc.T("Eng_Unmounted"));
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
        finally
        {
            _sessionOperations.Release();
        }
    }

    public async Task<EngineStatus> GetStatusAsync()
    {
        if (_tokens.IsAuthenticated && !IsSessionUnavailable())
        {
            await _profile.EnsureLoadedAsync();
            await StartOutboxForCurrentSessionAsync();
        }
        return Status(null);
    }

    public async Task<byte[]?> GetAvatarAsync()
    {
        var profileEpoch = _profile.Epoch;
        await _profile.EnsureLoadedAsync();
        var url = _profile.AvatarUrl;
        if (string.IsNullOrEmpty(url))
            return null;

        var avatar = await _gateway.DownloadAvatarAsync(url);
        return _profile.Epoch == profileEpoch ? avatar : null;
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

    internal async Task SessionRestoredAsync()
    {
        if (!_tokens.IsAuthenticated || IsSessionUnavailable())
            return;

        await _profile.EnsureLoadedAsync();
        await StartOutboxForCurrentSessionAsync(forceTrigger: true);

        if (!_tokens.IsAuthenticated || IsSessionUnavailable() || _mountedDriveLetter() is not null)
            return;

        var (letter, label) = _settings.GetLastMount();
        if (string.IsNullOrEmpty(letter))
            return;

        if (!string.IsNullOrEmpty(label))
            _fs.VolumeLabel = label;
        var status = await MountAsync(letter, label);
        if (status.Error is not null)
            EngineLog.Error("Автомонтаж после восстановления сессии", new InvalidOperationException(status.Error));
    }

    public async Task ShutdownAsync()
    {
        await _outboxWorker.StopAsync();
        try { _gateway.FlushPending(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { EngineLog.Error("Дренаж удалений при остановке", ex); }
        _unmount();
        try { _gateway.FlushPending(TimeSpan.FromSeconds(1), final: true); }
        catch (Exception ex) { EngineLog.Error("Финальный дренаж удалений при остановке", ex); }
        _tokens.Stop();
        _lifetime.Cancel();
    }

    private Task StartOutboxForCurrentSessionAsync(bool forceTrigger = false)
    {
        var trigger = false;
        lock (_outboxStateLock)
        {
            if (_sessionUnavailable || !_tokens.IsAuthenticated)
                return Task.CompletedTask;

            _outboxWorker.Start();
            var userId = _profile.UserId;
            if (!string.IsNullOrEmpty(userId)
                && (forceTrigger || !string.Equals(_outboxOwnerUserId, userId, StringComparison.Ordinal)))
            {
                _fs.AdoptOutboxForSession(userId);
                _outboxOwnerUserId = userId;
                trigger = true;
            }
        }

        if (trigger)
            _outboxWorker.Trigger();
        return Task.CompletedTask;
    }

    private bool IsSessionUnavailable()
    {
        lock (_outboxStateLock)
            return _sessionUnavailable;
    }

    private void SetSessionUnavailable(bool unavailable)
    {
        lock (_outboxStateLock)
            _sessionUnavailable = unavailable;
    }

    private EngineStatus Status(string? message, bool includeStorage = true)
    {
        var status = new EngineStatus
        {
            Authenticated = _tokens.IsAuthenticated,
            Mounted = _mountedDriveLetter() is not null,
            DriveLetter = _mountedDriveLetter(),
            Message = message,
            Username = _profile.Username,
            ServerHost = _serverHost,
            VolumeLabel = _fs.VolumeLabel,
            LastSyncError = _fs.LastSyncError,
            PendingUploads = _fs.PendingUploadCount,
        };

        if (includeStorage && _tokens.IsAuthenticated)
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

    private EngineStatus Error(Exception ex, bool includeStorage = true)
    {
        EngineLog.Error("DriveEngine", ex);
        var status = Status(null, includeStorage);
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

using BarkCloud.Drive.Contracts;

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
    private readonly string _serverHost;

    internal DriveEngine(TokenManager tokens, CloudGateway gateway, MountManager mount,
        BarkCloudFileSystem fs, CancellationTokenSource lifetime, EngineSettingsStore settings,
        UserProfile profile, string serverHost)
    {
        _tokens = tokens;
        _gateway = gateway;
        _mount = mount;
        _fs = fs;
        _lifetime = lifetime;
        _settings = settings;
        _profile = profile;
        _serverHost = serverHost;
    }

    public async Task<EngineStatus> LoginAsync(string login, string password, string? otpCode)
    {
        // Пустой логин → сервер кинул бы FailedPrecondition «Не передан ни логин ни email».
        // Отсекаем здесь: при восстановленной из refresh.bin сессии вход вообще не нужен.
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            return ErrorMessage("Введите логин и пароль");

        try
        {
            await _tokens.LoginAsync(login, password, otpCode);
            await _profile.EnsureLoadedAsync();
            return Status("Авторизация успешна");
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public Task<EngineStatus> LogoutAsync()
    {
        try
        {
            _mount.Unmount();
            _tokens.Logout();
            _profile.Clear();
            return Task.FromResult(Status("Выполнен выход"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
        }
    }

    public Task<EngineStatus> MountAsync(string driveLetter, string? volumeLabel)
    {
        try
        {
            if (!_tokens.IsAuthenticated)
                return Task.FromResult(ErrorMessage("Сначала выполните вход"));

            if (!string.IsNullOrWhiteSpace(volumeLabel))
                _fs.VolumeLabel = volumeLabel.Trim();

            _mount.Mount(driveLetter, _fs);
            return Task.FromResult(Status($"Примонтировано {driveLetter}:"));
        }
        catch (Exception ex) when (IsDokanMissing(ex))
        {
            return Task.FromResult(ErrorMessage(
                "Не найден драйвер Dokany (dokan2.dll). Установите Dokany 2.x — " +
                "github.com/dokan-dev/dokany/releases (DokanSetup.exe) — и перезапустите."));
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
                return Task.FromResult(ErrorMessage("Сначала выполните вход"));

            var letter = (string.IsNullOrWhiteSpace(driveLetter) ? _mount.DriveLetter : driveLetter.Trim())
                ?? throw new InvalidOperationException("Не задана буква диска");

            if (!string.IsNullOrWhiteSpace(volumeLabel))
                _fs.VolumeLabel = volumeLabel.Trim();

            if (_mount.IsMounted)
                _mount.Unmount();

            _mount.Mount(letter, _fs);
            return Task.FromResult(Status($"Перемонтировано {letter}: ({_fs.VolumeLabel})"));
        }
        catch (Exception ex) when (IsDokanMissing(ex))
        {
            return Task.FromResult(ErrorMessage(
                "Не найден драйвер Dokany (dokan2.dll). Установите Dokany 2.x и перезапустите."));
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
            _mount.Unmount();
            return Task.FromResult(Status("Отмонтировано"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(ex));
        }
    }

    public async Task<EngineStatus> GetStatusAsync()
    {
        if (_tokens.IsAuthenticated)
            await _profile.EnsureLoadedAsync();
        return Status(null);
    }

    public Task<EngineSettings> GetSettingsAsync()
        => Task.FromResult(new EngineSettings { CacheDir = _settings.GetCacheDir() });

    public Task<EngineSettings> SetCacheDirAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Пустой путь к папке кэша");

        _settings.SetCacheDir(path);
        _gateway.SetCacheDir(path);
        EngineLog.Info($"Папка кэша изменена: {path}");
        return Task.FromResult(new EngineSettings { CacheDir = path });
    }

    public Task ShutdownAsync()
    {
        _mount.Unmount();
        _lifetime.Cancel();
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
        };

        if (_tokens.IsAuthenticated)
        {
            try
            {
                var storage = _gateway.GetStorage();
                status.UsedBytes = storage.TotalUsedStorage;
                status.LimitBytes = storage.StorageLimit;
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

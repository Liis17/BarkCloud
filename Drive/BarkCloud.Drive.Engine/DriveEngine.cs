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

    internal DriveEngine(TokenManager tokens, CloudGateway gateway, MountManager mount,
        BarkCloudFileSystem fs, CancellationTokenSource lifetime)
    {
        _tokens = tokens;
        _gateway = gateway;
        _mount = mount;
        _fs = fs;
        _lifetime = lifetime;
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
            return Status("Авторизация успешна");
        }
        catch (Exception ex)
        {
            return Error(ex);
        }
    }

    public Task<EngineStatus> MountAsync(string driveLetter)
    {
        try
        {
            if (!_tokens.IsAuthenticated)
                return Task.FromResult(ErrorMessage("Сначала выполните вход"));

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

    public Task<EngineStatus> GetStatusAsync() => Task.FromResult(Status(null));

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

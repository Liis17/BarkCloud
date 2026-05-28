using BarkCloud.Users.Domain;

namespace BarkCloud.Users.Persistence.Services;

public interface IDevicesStorage
{
    Task<UserDevice> RegisterOrUpdateDevice(Guid deviceId, long userId, string originalName,
        string? appName, string? operationSystem, string? location);
    Task<List<UserDevice>> GetDevicesByUserId(long userId);
    Task<UserDevice?> GetDeviceById(Guid deviceId, long userId);
    Task RenameDevice(Guid deviceId, long userId, string customName);
    Task DeleteDevice(Guid deviceId, long userId);
    Task SetFirebaseToken(Guid deviceId, long userId, string? firebaseToken);
}

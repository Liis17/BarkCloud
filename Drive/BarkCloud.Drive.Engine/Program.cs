using System.Text.Json;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Engine;

// Один экземпляр движка на пользователя.
using var mutex = new Mutex(true, "BarkCloud.Drive.Engine.Singleton", out var isNew);
if (!isNew)
    return 0; // движок уже запущен — выходим, UI подключится к существующему

var config = LoadConfig();
var device = new DeviceIdentity();
var settings = new EngineSettingsStore();
var serverKey = EngineServerKey.FromConfig(config);

// Адреса по сервисам, как в iOS (nginx-порты): Identity :7020, Files :7025, Users :7021.
var identityAddress = $"https://{config.Host}:{config.IdentityPort}";
var filesAddress = $"https://{config.Host}:{config.FilesPort}";
var usersAddress = $"https://{config.Host}:{config.UsersPort}";

// tokenProvider читает текущий токен из TokenManager, который создаётся ниже —
// замыкание по ссылке разрывает циклическую зависимость канал↔токен-менеджер.
TokenManager? tokens = null;
using var connection = new BarkCloudConnection(
    identityAddress, filesAddress, usersAddress, config.DangerousAcceptAnyServerCert, device, () => tokens?.CurrentToken);
tokens = new TokenManager(connection.Identity, new TokenStore(serverKey: serverKey));
var profile = new UserProfile(connection.Users, userId => settings.SetLastUserId(serverKey, userId));
using var gateway = new CloudGateway(connection.Cloud, connection.Files, connection.Http, $"{filesAddress}/web",
    () => tokens?.CurrentToken, settings.GetCacheDir());
var outbox = new UploadOutbox(DriveStoragePaths.OutboxDirectory);
var fs = new BarkCloudFileSystem(gateway, outbox: outbox, serverKey: serverKey,
    ownerUserId: () => profile.UserId ?? settings.GetLastUserId(serverKey));
await using var outboxWorker = new UploadOutboxWorker(outbox,
    () => (serverKey, profile.UserId),
    (entry, cancellationToken) => fs.ProcessOutboxEntryAsync(entry, cancellationToken),
    completed: fs.CompleteOutboxEntry,
    failed: fs.SetOutboxSyncError,
    deferDataDeletion: fs.DeferOutboxDataDeletion);
fs.OutboxAdded = outboxWorker.Trigger;
fs.SyncError = fs.SetLastSyncError;
using var mount = new MountManager();
using var lifetime = new CancellationTokenSource();

var engine = new DriveEngine(tokens, gateway, mount, fs, lifetime, settings, profile, outboxWorker,
    serverKey, config.Host);
tokens.SessionRestored += engine.SessionRestoredAsync;

Console.WriteLine("BarkCloud.Drive.Engine запущен. Ожидание подключения UI...");

var restoreTask = tokens.TryRestoreAsync();
// IPC: named pipe + StreamJsonRpc. Обслуживаем по одному клиенту за раз,
// переживая переподключения UI. Выходим только по ShutdownAsync.
await EnginePipeServer.RunAsync(EnginePipe.Name, engine, lifetime.Token);

if (await Task.WhenAny(restoreTask, Task.Delay(TimeSpan.FromSeconds(3))) == restoreTask)
    await restoreTask;
mount.Unmount();
Console.WriteLine("BarkCloud.Drive.Engine завершён.");
return 0;

static EngineConfig LoadConfig()
{
    var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    var config = JsonSerializer.Deserialize<EngineConfig>(
                     File.ReadAllText(path),
                     new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                 ?? new EngineConfig();

    // Адреса self-hosted сервера, заданные пользователем в UI, перекрывают дефолты.
    var server = ServerConfig.Load();
    if (server != null)
    {
        config.Host = server.Host;
        config.IdentityPort = server.IdentityPort;
        config.FilesPort = server.FilesPort;
        config.UsersPort = server.UsersPort;
        config.DangerousAcceptAnyServerCert = server.AcceptAnyCert;
    }

    // Самодиагностика для self-hosted: какой адрес реально применён (частая причина проблем).
    EngineLog.Info(server != null
        ? $"Адреса из server.json: {config.Host} (Identity {config.IdentityPort}/Files {config.FilesPort}/Users {config.UsersPort})"
        : $"server.json не задан — дефолты: {config.Host} (Identity {config.IdentityPort}/Files {config.FilesPort}/Users {config.UsersPort})");

    return config;
}

internal sealed class EngineConfig
{
    public string Host { get; set; } = "cloud.barkfluff.com";
    public int IdentityPort { get; set; } = 7020; // nginx → Identity
    public int FilesPort { get; set; } = 7025;     // nginx → Files (Cloud/Files/Album + /web)
    public int UsersPort { get; set; } = 7021;     // nginx → Users
    public bool DangerousAcceptAnyServerCert { get; set; } = false;
}

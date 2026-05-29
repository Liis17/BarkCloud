using System.IO.Pipes;
using System.Text.Json;

using BarkCloud.Drive.Engine;

using StreamJsonRpc;

const string PipeName = "BarkCloud.Drive.Engine";

// Один экземпляр движка на пользователя.
using var mutex = new Mutex(true, "BarkCloud.Drive.Engine.Singleton", out var isNew);
if (!isNew)
    return 0; // движок уже запущен — выходим, UI подключится к существующему

var config = LoadConfig();
var device = new DeviceIdentity();

// Адреса по сервисам, как в iOS (nginx-порты): Identity :7020, Files :7025.
var identityAddress = $"https://{config.Host}:{config.IdentityPort}";
var filesAddress = $"https://{config.Host}:{config.FilesPort}";

// tokenProvider читает текущий токен из TokenManager, который создаётся ниже —
// замыкание по ссылке разрывает циклическую зависимость канал↔токен-менеджер.
TokenManager? tokens = null;
using var connection = new BarkCloudConnection(
    identityAddress, filesAddress, config.DangerousAcceptAnyServerCert, device, () => tokens?.CurrentToken);
tokens = new TokenManager(connection.Identity);

var gateway = new CloudGateway(connection.Cloud, connection.Files, connection.Http, $"{filesAddress}/web");
var fs = new BarkCloudFileSystem(gateway);
using var mount = new MountManager();
using var lifetime = new CancellationTokenSource();

var engine = new DriveEngine(tokens, gateway, mount, fs, lifetime);

Console.WriteLine("BarkCloud.Drive.Engine запущен. Ожидание подключения UI...");

// IPC: named pipe + StreamJsonRpc. Обслуживаем по одному клиенту за раз,
// переживая переподключения UI. Выходим только по ShutdownAsync.
while (!lifetime.IsCancellationRequested)
{
    var pipe = new NamedPipeServerStream(
        PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    try
    {
        await pipe.WaitForConnectionAsync(lifetime.Token);
    }
    catch (OperationCanceledException)
    {
        await pipe.DisposeAsync();
        break;
    }

    using (var rpc = JsonRpc.Attach(pipe, engine))
    {
        try { await rpc.Completion; }
        catch { /* клиент отключился — ждём следующего */ }
    }

    await pipe.DisposeAsync();
}

mount.Unmount();
Console.WriteLine("BarkCloud.Drive.Engine завершён.");
return 0;

static EngineConfig LoadConfig()
{
    var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    return JsonSerializer.Deserialize<EngineConfig>(
               File.ReadAllText(path),
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? new EngineConfig();
}

internal sealed class EngineConfig
{
    public string Host { get; set; } = "cloud.barkfluff.com";
    public int IdentityPort { get; set; } = 7020; // nginx → Identity
    public int FilesPort { get; set; } = 7025;     // nginx → Files (Cloud/Files/Album + /web)
    public bool DangerousAcceptAnyServerCert { get; set; } = true;
}

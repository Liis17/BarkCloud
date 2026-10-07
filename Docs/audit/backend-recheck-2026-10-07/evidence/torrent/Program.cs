using System.Reflection;
using System.Security.Claims;
using BarkCloud.GrpcServer.Metrics;
using BarkCloud.GrpcServer.XAuth;
using BarkCloud.Proto.Torrent;
using BarkCloud.Shared.Identity;
using BarkCloud.TestKit;
using BarkCloud.Torrent.Domain;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MonoTorrent.Client;

var root = Path.Combine(Path.GetTempPath(), "barkcloud-torrent-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await Run(root);
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task Run(string root)
{
var connection = "Data Source=" + Path.Combine(root, "audit.db");
var fault = new SaveFault();
DbContextOptions<TorrentContext> Options(params IInterceptor[] interceptors) => new DbContextOptionsBuilder<TorrentContext>().UseSqlite(connection).AddInterceptors(interceptors).Options;
var id = Guid.NewGuid();
await using (var db = new TorrentContext(Options()))
{
    await db.Database.EnsureCreatedAsync();
    db.Torrents.Add(new TorrentEntity { Id = id, UserId = 7, Downloaded = 100, Uploaded = 200, AddedAt = DateTime.UtcNow });
    await db.SaveChangesAsync();
}
await using var engine = new ProbeEngine();
await engine.InitializeAsync(Path.Combine(root, "cache"), 0);
var managed = await engine.AddMagnetAsync(id, "magnet:?xt=urn:btih:0123456789012345678901234567890123456789&dn=audit", Path.Combine(root, "downloads"), false);
using var provider = new ServiceCollection().AddScoped(_ => new TorrentContext(Options(fault))).BuildServiceProvider();
var persistence = new TorrentPersistenceService(engine, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<TorrentPersistenceService>.Instance);
var flush = typeof(TorrentPersistenceService).GetMethod("FlushAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
Task Flush() => (Task)flush.Invoke(persistence, [CancellationToken.None])!;
void AddTraffic(string property, int value)
{
    var monitor = managed.Manager.Monitor;
    var speed = monitor.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(monitor)!;
    speed.GetType().GetMethod("AddDelta", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(speed, [value]);
}
async Task<TorrentEntity> Read()
{
    await using var db = new TorrentContext(Options());
    return await db.Torrents.AsNoTracking().SingleAsync();
}
AddTraffic("DataDown", 10);
AddTraffic("DataUp", 20);
fault.FailNext = true;
try { await Flush(); throw new Exception("Expected injected DB failure"); }
catch (DbUpdateException) { }
var failed = await Read();
Console.WriteLine($"F21 failed flush: DB={failed.Downloaded}/{failed.Uploaded}, baseline={managed.LastSessionDownloaded}/{managed.LastSessionUploaded}, monitor={managed.Manager.Monitor.DataBytesReceived}/{managed.Manager.Monitor.DataBytesSent}");
if (failed.Downloaded != 100 || failed.Uploaded != 200 || managed.LastSessionDownloaded != 10 || managed.LastSessionUploaded != 20) throw new Exception("F21 setup failed");
AddTraffic("DataDown", 5);
AddTraffic("DataUp", 7);
await Flush();
var retried = await Read();
Console.WriteLine($"F21 retry: DB={retried.Downloaded}/{retried.Uploaded}, expected=115/227, lost=10/20");
if (retried.Downloaded != 105 || retried.Uploaded != 207) throw new Exception("F21 result differs");

var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(IdentityClaims.UserId, "7"), new Claim(IdentityClaims.TokenType, nameof(TokenType.User))], "probe"));
var user = new UserContext(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });
TorrentApiService Service(TorrentContext db) => new(user, new TokenRevocationCache(), new TorrentStore(db), engine, null!, null!, new ConfigurationBuilder().Build(), new MetricsCollector());
var request = new TorrentIdRequest { Id = id.ToString() };
var call = new TestServerCallContext();
var pauseBarrier = new SaveBarrier();
await using (var pauseDb = new TorrentContext(Options(pauseBarrier)))
await using (var resumeDb = new TorrentContext(Options()))
{
    var pause = Service(pauseDb).PauseTorrent(request, call);
    await pauseBarrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Service(resumeDb).ResumeTorrent(request, call);
    pauseBarrier.Release.SetResult();
    await pause;
}
var afterRace = await Read();
Console.WriteLine($"F22 Pause->Resume engine, delayed Pause commit: DB.Paused={afterRace.Paused}, successful engine action Paused={engine.Paused}");
if (!afterRace.Paused || engine.Paused) throw new Exception("F22 race not reproduced");

await using (var db = new TorrentContext(Options()))
{
    (await db.Torrents.SingleAsync()).Paused = false;
    await db.SaveChangesAsync();
}
fault.FailNext = true;
await using (var db = new TorrentContext(Options(fault)))
{
    try { await Service(db).PauseTorrent(request, call); throw new Exception("Expected DB failure"); }
    catch (DbUpdateException) { }
}
var afterFailure = await Read();
Console.WriteLine($"F22 Pause DB failure: DB.Paused={afterFailure.Paused}, successful engine action Paused={engine.Paused}");
if (afterFailure.Paused || !engine.Paused) throw new Exception("F22 failure not reproduced");
Console.WriteLine("3 residual defect probes reproduced; production code unchanged.");

}

sealed class ProbeEngine() : TorrentEngineService(NullLogger<TorrentEngineService>.Instance)
{
    public bool Paused { get; private set; }
    protected override Task PauseManagerAsync(TorrentManager manager) { Paused = true; return Task.CompletedTask; }
    protected override Task StartManagerAsync(TorrentManager manager) { Paused = false; return Task.CompletedTask; }
}
sealed class SaveFault : SaveChangesInterceptor
{
    public bool FailNext;
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (FailNext) { FailNext = false; throw new DbUpdateException("Injected pre-commit DB failure"); }
        return ValueTask.FromResult(result);
    }
}
sealed class SaveBarrier : SaveChangesInterceptor
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using Microsoft.AspNetCore.Mvc;
using BarkCloud.Shared.Identity;
using BarkCloud.Torrent.Host;
using BarkCloud.Torrent.Infrastructure;
using BarkCloud.Torrent.Persistence;

internal static class Program
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), $"barkcloud-audit-torrent-{Guid.NewGuid():N}");

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(Scratch);
        var assembly = typeof(Torrent).Assembly;
        Console.WriteLine($"MonoTorrent package=3.0.2 assembly={assembly.GetName().Version} fileVersion={FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion} location={assembly.Location}");
        Console.WriteLine($"Scratch: {Path.GetFullPath(Scratch)}");
        Console.WriteLine("Safety: EngineSettings.CacheDirectory is scratch; listeners and DHT disabled; no StartAsync called.");

        var controllerSentinel = Path.Combine(Scratch, "controller-sentinel.txt");
        await File.WriteAllTextAsync(controllerSentinel, "scratch-only sentinel");
        var userOutsideSentinel = Path.Combine(Scratch, "users", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(userOutsideSentinel)!);
        await File.WriteAllTextAsync(userOutsideSentinel, "scratch-only v2 sibling sentinel");
        var rootedV1Dir = Path.Combine(Scratch, "rooted-v1");
        var rootedV2Dir = Path.Combine(Scratch, "rooted-v2");
        Directory.CreateDirectory(rootedV1Dir);
        Directory.CreateDirectory(rootedV2Dir);
        await File.WriteAllTextAsync(Path.Combine(rootedV1Dir, "sentinel.txt"), "scratch-only v1 sentinel");
        await File.WriteAllTextAsync(Path.Combine(rootedV2Dir, "sentinel.txt"), "scratch-only v2 sentinel");

        var tests = new[]
        {
            new MetadataCase("v1-multifile-dotdot", V1Multi("safe-root", "..", "sentinel.txt")),
            new MetadataCase("v1-single-name-dotdot", V1Single("..")),
            new MetadataCase("v1-single-name-traversal", V1Single(Path.Combine("..", "controller-sentinel.txt"))),
            new MetadataCase("v1-multifile-rooted-name", V1Multi(rootedV1Dir, "sentinel.txt")),
            new MetadataCase("v1-single-rooted-name", V1Single(controllerSentinel)),
            new MetadataCase("v1-rooted-path-component", V1Multi("safe-root", controllerSentinel)),
            new MetadataCase("v2-filetree-dotdot", V2("safe-root", "..", "sentinel.txt")),
            new MetadataCase("v2-single-name-dotdot", V2("..")),
            new MetadataCase("v2-multifile-rooted-name", V2(rootedV2Dir, "sentinel.txt")),
            new MetadataCase("v2-single-rooted-name", V2(Path.Combine(Scratch, "v2-single-rooted.txt"))),
            new MetadataCase("v2-rooted-filetree-component", V2("safe-root", controllerSentinel)),
        };

        var loadAccepted = 0;
        var managersAdded = 0;
        foreach (var test in tests)
        {
            Console.WriteLine();
            Console.WriteLine($"CASE {test.Name}");
            Torrent torrent;
            try
            {
                torrent = await Torrent.LoadAsync(test.Bytes);
                loadAccepted++;
                Console.WriteLine($"  LOAD=accepted name={Escape(torrent.Name)} files={torrent.Files.Count} infohash={torrent.InfoHashes.V1OrV2.ToHex()}");
                foreach (var file in torrent.Files)
                    Console.WriteLine($"  TorrentFile path={Escape(file.Path.ToString())} length={file.Length}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  LOAD=rejected {ex.GetType().Name}: {Escape(ex.Message)}");
                continue;
            }

            var savePath = Path.Combine(Scratch, "managers", test.Name, "owned");
            var cachePath = Path.Combine(Scratch, "engine-cache", test.Name);
            Directory.CreateDirectory(savePath);
            Directory.CreateDirectory(cachePath);
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = cachePath,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadDhtCache = false,
                AutoSaveLoadMagnetLinkMetadata = false,
                DhtEndPoint = null,
                ListenEndPoints = new Dictionary<string, IPEndPoint>(),
                AllowLocalPeerDiscovery = false,
                AllowPortForwarding = false,
            }.ToSettings();

            using var engine = new ClientEngine(settings);
            try
            {
                var manager = await engine.AddAsync(torrent, savePath);
                managersAdded++;
                Console.WriteLine($"  MANAGER=added state={manager.State} files={manager.Files.Count}; StartAsync=not-called");
                for (var i = 0; i < manager.Files.Count; i++)
                {
                    var file = manager.Files[i];
                    var fullPath = Path.GetFullPath(file.FullPath);
                    Console.WriteLine($"  ManagerFile[{i}] path={Escape(file.Path.ToString())} full={Escape(file.FullPath)} canonical={Escape(fullPath)} withinScratch={IsWithinScratch(fullPath)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  MANAGER=rejected {ex.GetType().Name}: {Escape(ex.Message)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("CASE controller-fallback-dotdot (linked real TorrentController.cs)");
        var userSavePath = Path.Combine(Scratch, "users", "42");
        Directory.CreateDirectory(userSavePath);
        var entity = new TorrentEntity { SavePath = userSavePath };
        entity.Files.Add(new TorrentFileEntity { Index = 0, Path = Path.Combine("..", "sentinel.txt") });
        entity.Files.Add(new TorrentFileEntity { Index = 1, Path = controllerSentinel });
        var controller = new TorrentController(new UserContext { UserId = 42 }, new OwnedStore(entity, 42), new TorrentEngineService());
        var traversalResult = await controller.Download(Guid.NewGuid(), 0);
        var rootedResult = await controller.Download(Guid.NewGuid(), 1);
        var controllerPass = ReportPhysical("v2-metadata-relative-..", traversalResult, userSavePath, userOutsideSentinel)
            & ReportPhysical("rooted-row-path", rootedResult, userSavePath, controllerSentinel);

        var sentinelsIntact = await File.ReadAllTextAsync(controllerSentinel) == "scratch-only sentinel"
            && await File.ReadAllTextAsync(userOutsideSentinel) == "scratch-only v2 sibling sentinel"
            && await File.ReadAllTextAsync(Path.Combine(rootedV1Dir, "sentinel.txt")) == "scratch-only v1 sentinel"
            && await File.ReadAllTextAsync(Path.Combine(rootedV2Dir, "sentinel.txt")) == "scratch-only v2 sentinel";
        Console.WriteLine();
        Console.WriteLine($"SUMMARY loadAccepted={loadAccepted}/{tests.Length} managersAdded={managersAdded} controllerFallbackEscapesUserDir={controllerPass} sentinelsIntact={sentinelsIntact}");
        Console.WriteLine("No torrent manager was started; no peer/tracker/DHT request or payload write was initiated.");
        return controllerPass && sentinelsIntact ? 0 : 1;
    }

    private static bool ReportPhysical(string label, IActionResult result, string userSavePath, string sentinel)
    {
        if (result is not PhysicalFileResult physical)
        {
            Console.WriteLine($"  {label}: result={result.GetType().Name}");
            return false;
        }

        var canonical = Path.GetFullPath(physical.FileName);
        var outsideUserDir = !IsWithin(Path.GetFullPath(userSavePath), canonical);
        var correctSentinel = string.Equals(canonical, Path.GetFullPath(sentinel), StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  {label}: PhysicalFile.FileName={Escape(physical.FileName)} canonical={Escape(canonical)} outsideUserDir={outsideUserDir} sentinel={correctSentinel}");
        return outsideUserDir && correctSentinel;
    }

    private static bool IsWithinScratch(string path) => IsWithin(Path.GetFullPath(Scratch), path);

    private static bool IsWithin(string directory, string path)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value) => value.Replace(Path.DirectorySeparatorChar, '/');

    private static byte[] V1Single(string name)
    {
        var info = Dict(
            ("length", Integer(1)),
            ("name", Bytes(name)),
            ("piece length", Integer(16384)),
            ("pieces", Bytes(SHA1.HashData(new byte[] { 0x41 }))));
        return Dict(("info", info));
    }

    private static byte[] V1Multi(string name, params string[] path)
    {
        var file = Dict(
            ("length", Integer(1)),
            ("path", List(path.Select(Bytes).ToArray())));
        var info = Dict(
            ("files", List(file)),
            ("name", Bytes(name)),
            ("piece length", Integer(16384)),
            ("pieces", Bytes(SHA1.HashData(new byte[] { 0x41 }))));
        return Dict(("info", info));
    }

    private static byte[] V2(string name, params string[] path)
    {
        var block = new byte[16384];
        block[0] = 0x41;
        var leaf = Dict(("", Dict(
            ("length", Integer(1)),
            ("pieces root", Bytes(SHA256.HashData(block))))));
        byte[] tree = leaf;
        foreach (var component in path.Reverse())
            tree = Dict((component, tree));

        var info = Dict(
            ("file tree", tree),
            ("meta version", Integer(2)),
            ("name", Bytes(name)),
            ("piece length", Integer(16384)));
        return Dict(("info", info));
    }

    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($"i{value}e");

    private static byte[] Bytes(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    private static byte[] Bytes(byte[] value) => Join(Encoding.ASCII.GetBytes($"{value.Length}:"), value);

    private static byte[] List(params byte[][] values) => Join(new[] { new byte[] { (byte)'l' } }.Concat(values).Concat(new[] { new byte[] { (byte)'e' } }).ToArray());

    private static byte[] Dict(params (string Key, byte[] Value)[] entries)
    {
        var sorted = entries.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var parts = new List<byte[]> { new byte[] { (byte)'d' } };
        foreach (var (key, value) in sorted)
        {
            parts.Add(Bytes(key));
            parts.Add(value);
        }
        parts.Add(new byte[] { (byte)'e' });
        return Join(parts.ToArray());
    }

    private static byte[] Join(params byte[][] arrays)
    {
        using var stream = new MemoryStream();
        foreach (var bytes in arrays)
            stream.Write(bytes, 0, bytes.Length);
        return stream.ToArray();
    }

    private sealed record MetadataCase(string Name, byte[] Bytes);
}

namespace BarkCloud.Shared.Identity
{
    public sealed class UserContext { public long UserId { get; init; } }
    public enum TokenType { User }
}

namespace BarkCloud.GrpcServer.XAuth
{
    public sealed class ScratchNamespaceAnchor { }
}

namespace BarkCloud.Torrent.Persistence
{
    public interface ITorrentStore { Task<TorrentEntity?> Get(Guid id, long userId); }
    public sealed class TorrentEntity
    {
        public string SavePath { get; set; } = "";
        public List<TorrentFileEntity> Files { get; } = new();
    }
    public sealed class TorrentFileEntity
    {
        public int Index { get; set; }
        public string Path { get; set; } = "";
    }
    public sealed class OwnedStore(TorrentEntity entity, long owner) : ITorrentStore
    {
        public Task<TorrentEntity?> Get(Guid id, long userId) =>
            Task.FromResult<TorrentEntity?>(userId == owner ? entity : null);
    }
}

namespace BarkCloud.Torrent.Infrastructure
{
    public sealed class TorrentEngineService
    {
        public ManagedTorrent? Get(Guid id) => null;
    }
    public sealed class ManagedTorrent
    {
        public Manager Manager { get; } = new();
    }
    public sealed class Manager
    {
        public List<ManagerFile> Files { get; } = new();
    }
    public sealed class ManagerFile { public string FullPath { get; set; } = ""; }
}







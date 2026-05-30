using DokanNet;
using DokanNet.Logging;

namespace BarkCloud.Drive.Engine;

// Монтирование/размонтирование диска через Dokany. Сам диск живёт на фоновых
// потоках Dokan, пока DokanInstance не освобождён — поэтому процесс движка может
// обслуживать IPC, а диск оставаться примонтированным.
internal sealed class MountManager : IDisposable
{
    private readonly object _lock = new();
    private Dokan? _dokan;
    private DokanInstance? _instance;
    private string? _mountPoint;

    public bool IsMounted
    {
        get { lock (_lock) return _instance != null; }
    }

    public string? DriveLetter
    {
        get { lock (_lock) return _mountPoint?[..1]; }
    }

    public void Mount(string driveLetter, IDokanOperations fs)
    {
        lock (_lock)
        {
            if (_instance != null)
                throw new InvalidOperationException("Диск уже примонтирован");

            var mountPoint = $"{driveLetter}:\\";
            var dokan = new Dokan(new NullLogger());
            var instance = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(options =>
                {
                    options.Options = DokanOptions.FixedDrive;
                    options.MountPoint = mountPoint;
                })
                .Build(fs);

            _dokan = dokan;
            _instance = instance;
            _mountPoint = mountPoint;
        }
    }

    public void Unmount()
    {
        lock (_lock)
        {
            if (_instance == null)
                return;

            _dokan!.RemoveMountPoint(_mountPoint!);
            _instance.Dispose();
            _dokan.Dispose();

            _instance = null;
            _dokan = null;
            _mountPoint = null;
        }
    }

    public void Dispose() => Unmount();
}

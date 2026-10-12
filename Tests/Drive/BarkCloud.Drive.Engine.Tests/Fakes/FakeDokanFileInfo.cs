using System.Security.Principal;
using System.Threading;

using DokanNet;

namespace BarkCloud.Drive.Engine.Tests.Fakes;

internal sealed class FakeDokanFileInfo : IDokanFileInfo
{
    private int _tryResetTimeoutCalls;

    public object? Context { get; set; }
    public bool DeletePending { get; set; }
    public bool IsDirectory { get; set; }
    public bool NoCache { get; set; }
    public bool PagingIo { get; set; }
    public int ProcessId { get; set; }
    public bool SynchronousIo { get; set; }
    public bool WriteToEndOfFile { get; set; }
    public int TryResetTimeoutCalls => Volatile.Read(ref _tryResetTimeoutCalls);
    public int LastTimeoutMilliseconds { get; private set; }

    public WindowsIdentity GetRequestor() => null!;

    public bool TryResetTimeout(int milliseconds)
    {
        Interlocked.Increment(ref _tryResetTimeoutCalls);
        LastTimeoutMilliseconds = milliseconds;
        return true;
    }
}

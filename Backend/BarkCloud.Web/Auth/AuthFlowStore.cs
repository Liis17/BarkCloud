using System.Security.Cryptography;

namespace BarkCloud.Web.Auth;

public enum AuthFlowKind
{
    Login,
    Registration,
    PasswordReset,
    SessionPending
}

/// <summary>Short-lived server-side state for one authentication tab.</summary>
public sealed class AuthFlow
{
    internal AuthFlow(string flowId, string deviceId, AuthFlowKind kind, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        FlowId = flowId;
        DeviceId = deviceId;
        Kind = kind;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Screen = kind switch
        {
            AuthFlowKind.Login => "login",
            AuthFlowKind.Registration => "register",
            AuthFlowKind.PasswordReset => "forgot",
            _ => "session-pending"
        };
    }

    public string FlowId { get; }
    internal string DeviceId { get; }
    public AuthFlowKind Kind { get; internal set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string Screen { get; internal set; }
    public string? Login { get; internal set; }
    public string? Password { get; internal set; }
    public string? Username { get; internal set; }
    public string? Email { get; internal set; }
    public string? FirstName { get; internal set; }
    public string? LastName { get; internal set; }
    public string? CodeId { get; internal set; }
    public string? ResetId { get; internal set; }
    public string? RefreshToken { get; internal set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; internal set; }
    public bool Remember { get; internal set; } = true;
    public bool RevokeOtherSessions { get; internal set; } = true;
    public string? OtpMethod { get; internal set; }
    public string? OtpMailStatus { get; internal set; }
    public DateTimeOffset? CodeExpiresAt { get; internal set; }
    public DateTimeOffset? RetryAt { get; internal set; }
    public string? FormError { get; internal set; }
    public string? FormMessage { get; internal set; }
    internal Dictionary<string, string> FieldErrors { get; } = new(StringComparer.Ordinal);
    internal SemaphoreSlim OperationLock { get; } = new(1, 1);
    internal CancellationTokenSource LifetimeCancellation { get; } = new();
    internal int ActiveLeases { get; set; }
    internal bool Inactive { get; set; }

    internal void ClearSecrets()
    {
        Login = null;
        Password = null;
        Username = null;
        Email = null;
        FirstName = null;
        LastName = null;
        CodeId = null;
        ResetId = null;
        RefreshToken = null;
        RefreshTokenExpiresAt = null;
        OtpMethod = null;
        OtpMailStatus = null;
        CodeExpiresAt = null;
        RetryAt = null;
        FormError = null;
        FormMessage = null;
        FieldErrors.Clear();
    }
}

/// <summary>
/// Bounded, process-local auth flow cache. Expiry is absolute; normal updates and resend do not extend it.
/// </summary>
public sealed class AuthFlowStore : IDisposable
{
    public const int Capacity = 1024;
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private readonly Dictionary<string, AuthFlow> _flows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly ITimer _cleanupTimer;

    public AuthFlowStore(TimeProvider? timeProvider = null, TimeSpan? lifetime = null, TimeSpan? cleanupInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? AbsoluteLifetime;
        var interval = cleanupInterval ?? TimeSpan.FromSeconds(30);
        _cleanupTimer = _timeProvider.CreateTimer(_ => ExpireDueFlows(), null, interval, interval);
    }

    public int ActiveCount
    {
        get
        {
            ExpireDueFlows();
            lock (_sync)
            {
                return _flows.Count;
            }
        }
    }

    public bool TryCreate(AuthFlowKind kind, string deviceId, out AuthFlow? flow)
    {
        ExpireDueFlows();
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (_flows.Count >= Capacity)
            {
                flow = null;
                return false;
            }

            flow = new AuthFlow(CreateId(), deviceId, kind, now, now.Add(_lifetime));
            _flows.Add(flow.FlowId, flow);
            return true;
        }
    }

    public async Task<AuthFlowLease?> AcquireAsync(string flowId, string deviceId, AuthFlowKind expectedKind,
        CancellationToken cancellationToken = default)
    {
        ExpireDueFlows();
        AuthFlow? flow;
        lock (_sync)
        {
            if (!_flows.TryGetValue(flowId, out flow)
                || flow.Kind != expectedKind
                || !string.Equals(flow.DeviceId, deviceId, StringComparison.Ordinal))
                return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, flow.LifetimeCancellation.Token);
        try
        {
            await flow.OperationLock.WaitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && flow.LifetimeCancellation.IsCancellationRequested)
        {
            return null;
        }

        ExpireDueFlows();
        var acquired = false;
        lock (_sync)
        {
            if (!_flows.TryGetValue(flowId, out var current)
                || !ReferenceEquals(current, flow)
                || flow.Kind != expectedKind
                || flow.ExpiresAt <= _timeProvider.GetUtcNow()
                || !string.Equals(flow.DeviceId, deviceId, StringComparison.Ordinal))
            {
                acquired = false;
            }
            else
            {
                flow.ActiveLeases++;
                acquired = true;
            }
        }

        if (!acquired)
        {
            ExpireDueFlows();
            flow.OperationLock.Release();
            return null;
        }

        return new AuthFlowLease(this, flow, cancellationToken);
    }

    public bool IsActive(AuthFlow flow)
    {
        ExpireDueFlows();
        lock (_sync)
        {
            return _flows.TryGetValue(flow.FlowId, out var current) && ReferenceEquals(current, flow);
        }
    }

    public void Complete(AuthFlow flow)
    {
        var clearNow = false;
        lock (_sync)
        {
            flow.Inactive = true;
            _flows.Remove(flow.FlowId);
            clearNow = flow.ActiveLeases == 0;
        }
        flow.LifetimeCancellation.Cancel();
        if (clearNow) flow.ClearSecrets();
    }

    public void ExpireDueFlows()
    {
        List<AuthFlow> expired;
        lock (_sync)
        {
            expired = RemoveExpired(_timeProvider.GetUtcNow());
        }

        foreach (var flow in expired)
        {
            flow.LifetimeCancellation.Cancel();
            lock (_sync)
            {
                if (flow.ActiveLeases == 0)
                    flow.ClearSecrets();
            }
        }
    }

    private List<AuthFlow> RemoveExpired(DateTimeOffset now)
    {
        var expired = new List<AuthFlow>();
        foreach (var (id, flow) in _flows.ToArray())
        {
            if (flow.ExpiresAt <= now)
            {
                flow.Inactive = true;
                _flows.Remove(id);
                expired.Add(flow);
            }
        }
        return expired;
    }

    internal void Release(AuthFlow flow)
    {
        lock (_sync)
        {
            flow.ActiveLeases--;
            if (flow.ActiveLeases < 0)
                throw new InvalidOperationException("An auth flow lease was released more than once.");
            if (flow.Inactive && flow.ActiveLeases == 0)
                flow.ClearSecrets();
        }

        flow.OperationLock.Release();
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        AuthFlow[] flows;
        lock (_sync)
        {
            flows = _flows.Values.ToArray();
            _flows.Clear();
            foreach (var flow in flows) flow.Inactive = true;
        }

        foreach (var flow in flows)
        {
            flow.LifetimeCancellation.Cancel();
            if (flow.ActiveLeases == 0) flow.ClearSecrets();
        }
    }

    private static string CreateId()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class AuthFlowLease(AuthFlowStore store, AuthFlow flow, CancellationToken requestCancellation) : IDisposable
{
    public AuthFlow Flow { get; } = flow;
    public CancellationToken CancellationToken => Flow.LifetimeCancellation.Token;
    public CancellationToken RequestCancellation { get; } = requestCancellation;

    public void Dispose() => store.Release(Flow);
}

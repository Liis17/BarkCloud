using System.Threading.Channels;

using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Identity;

using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class TokenManagerTests
{
    private const string AuthMethod = "/barkcloud.identity.IdentityApi/Auth";
    private const string CreateTokenMethod = "/barkcloud.identity.IdentityApi/CreateToken";

    [Fact]
    public async Task Direct_restore_notifies_once_after_the_first_refresh()
    {
        using var temp = new TestDirectory();
        var time = new RecordingTimeProvider();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("saved-refresh");
        var invoker = new FakeCallInvoker();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return CreateResponse(time, "restored-access", TimeSpan.FromSeconds(90));

            refreshStarted.TrySetResult();
            return CreateResponse(time, "refreshed-access", TimeSpan.FromHours(1));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);
        var notifications = 0;
        tokens.SessionRestored += () =>
        {
            Interlocked.Increment(ref notifications);
            return Task.CompletedTask;
        };

        try
        {
            await tokens.TryRestoreAsync();
            (await time.NextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(TimeSpan.FromSeconds(30));

            var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(31)));
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await advance.WaitAsync(TimeSpan.FromSeconds(5));
            (await time.NextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(TimeSpan.FromMinutes(59));

            notifications.Should().Be(1);
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Try_restore_keeps_refresh_token_and_retries_transient_failure()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("saved-refresh");
        var invoker = new FakeCallInvoker();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionRestored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new RpcException(new Status(StatusCode.Unavailable, "offline"));

            retryStarted.TrySetResult();
            return CreateResponse(time, "restored-access", TimeSpan.FromHours(1));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);
        tokens.SessionRestored += () =>
        {
            sessionRestored.TrySetResult();
            return Task.CompletedTask;
        };

        try
        {
            await tokens.TryRestoreAsync();

            store.LoadRefreshToken().Should().Be("saved-refresh");
            tokens.IsAuthenticated.Should().BeFalse();
            calls.Should().Be(1);

            time.Advance(TimeSpan.FromSeconds(5));
            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sessionRestored.Task.WaitAsync(TimeSpan.FromSeconds(5));

            tokens.CurrentToken.Should().Be("restored-access");
            store.LoadRefreshToken().Should().Be("saved-refresh");
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Refresh_transient_error_keeps_session_and_retries()
    {
        using var temp = new TestDirectory();
        var time = new RetryObservedTimeProvider();
        var invoker = new FakeCallInvoker();
        var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod,
            _ => LoginResponse(time, "initial-access", "refresh", TimeSpan.FromSeconds(90)));
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstRefresh.TrySetResult();
                throw new RpcException(new Status(StatusCode.Unavailable, "offline"));
            }

            retryRefresh.TrySetResult();
            return CreateResponse(time, "renewed-access", TimeSpan.FromHours(1));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker),
            new TokenStore(temp.Path("tokens")), time);

        try
        {
            (await tokens.LoginAsync("user", "password", null)).Should().BeTrue();
            var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(30)));
            await firstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await advance.WaitAsync(TimeSpan.FromSeconds(5));

            tokens.CurrentToken.Should().Be("initial-access");
            tokens.IsAuthenticated.Should().BeTrue();

            await time.RetryDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(4));
            retryRefresh.Task.IsCompleted.Should().BeFalse();
            time.Advance(TimeSpan.FromSeconds(1));
            await retryRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tokens.CurrentToken.Should().Be("renewed-access");
            tokens.IsAuthenticated.Should().BeTrue();
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_without_refresh_cancellation_is_retried(bool operationCancelled)
    {
        using var temp = new TestDirectory();
        var time = new RetryObservedTimeProvider();
        var invoker = new FakeCallInvoker();
        var firstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod,
            _ => LoginResponse(time, "initial-access", "refresh", TimeSpan.FromSeconds(90)));
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstRefresh.TrySetResult();
                throw operationCancelled ? new OperationCanceledException() : new RpcException(new Status(StatusCode.Cancelled, "cancelled by transport"));
            }

            retryRefresh.TrySetResult();
            return CreateResponse(time, "renewed-access", TimeSpan.FromHours(1));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker),
            new TokenStore(temp.Path("tokens")), time);

        try
        {
            (await tokens.LoginAsync("user", "password", null)).Should().BeTrue();
            var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(30)));
            await firstRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await advance.WaitAsync(TimeSpan.FromSeconds(5));

            await time.RetryDelayScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(5));
            await retryRefresh.Task.WaitAsync(TimeSpan.FromSeconds(5));

            tokens.CurrentToken.Should().Be("renewed-access");
            tokens.IsAuthenticated.Should().BeTrue();
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Late_login_success_after_logout_is_ignored()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod, _ =>
        {
            started.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return LoginResponse(time, "late-access", "late-refresh", TimeSpan.FromHours(1));
        });
        var store = new TokenStore(temp.Path("tokens"), "server");
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        var login = StartLongRunning(() => tokens.LoginAsync("user", "password", null).GetAwaiter().GetResult());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tokens.Logout();
        release.TrySetResult();

        (await login.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        tokens.CurrentToken.Should().BeNull();
        store.LoadRefreshToken().Should().BeNull();
    }

    [Fact]
    public async Task Late_login_rejection_after_new_login_does_not_clear_new_tokens()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var invoker = new FakeCallInvoker();
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                throw FakeCallInvoker.BusinessError(ServerErrors.InvalidRefreshToken);
            }

            return LoginResponse(time, "new-access", "new-refresh", TimeSpan.FromHours(1));
        });
        var store = new TokenStore(temp.Path("tokens"), "server");
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        try
        {
            var firstLogin = StartLongRunning(
                () => tokens.LoginAsync("first", "password", null).GetAwaiter().GetResult());
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await tokens.LoginAsync("second", "password", null)).Should().BeTrue();
            releaseFirst.TrySetResult();

            var act = async () => await firstLogin.WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<RpcException>();
            tokens.CurrentToken.Should().Be("new-access");
            store.LoadRefreshToken().Should().Be("new-refresh");
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Late_restore_success_after_new_login_is_ignored()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("old-refresh");
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            restoreStarted.TrySetResult();
            releaseRestore.Task.GetAwaiter().GetResult();
            return CreateResponse(time, "old-access", TimeSpan.FromHours(1));
        });
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod,
            _ => LoginResponse(time, "new-access", "new-refresh", TimeSpan.FromHours(1)));
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        try
        {
            var restore = StartLongRunning(() => tokens.TryRestoreAsync().GetAwaiter().GetResult());
            await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await tokens.LoginAsync("new", "password", null)).Should().BeTrue();
            releaseRestore.TrySetResult();
            await restore.WaitAsync(TimeSpan.FromSeconds(5));

            tokens.CurrentToken.Should().Be("new-access");
            store.LoadRefreshToken().Should().Be("new-refresh");
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Late_restore_rejection_after_new_login_does_not_clear_new_tokens()
    {
        using var temp = new TestDirectory();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("old-refresh");
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = NewTime();
        var invoker = new FakeCallInvoker();
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            restoreStarted.TrySetResult();
            releaseRestore.Task.GetAwaiter().GetResult();
            throw FakeCallInvoker.BusinessError(ServerErrors.InvalidRefreshToken);
        });
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod,
            _ => LoginResponse(time, "new-access", "new-refresh", TimeSpan.FromHours(1)));
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        try
        {
            var restore = StartLongRunning(() => tokens.TryRestoreAsync().GetAwaiter().GetResult());
            await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await tokens.LoginAsync("new", "password", null)).Should().BeTrue();
            releaseRestore.TrySetResult();
            await restore.WaitAsync(TimeSpan.FromSeconds(5));

            tokens.CurrentToken.Should().Be("new-access");
            store.LoadRefreshToken().Should().Be("new-refresh");
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Late_refresh_success_after_new_login_is_ignored()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        var authCalls = 0;
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod, _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            return call == 1
                ? LoginResponse(time, "old-access", "old-refresh", TimeSpan.FromSeconds(90))
                : LoginResponse(time, "new-access", "new-refresh", TimeSpan.FromHours(1));
        });
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            refreshStarted.TrySetResult();
            releaseRefresh.Task.GetAwaiter().GetResult();
            refreshReturned.TrySetResult();
            return CreateResponse(time, "late-refresh-access", TimeSpan.FromHours(1));
        });
        var store = new TokenStore(temp.Path("tokens"), "server");
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        try
        {
            (await tokens.LoginAsync("old", "password", null)).Should().BeTrue();
            var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(30)));
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await tokens.LoginAsync("new", "password", null)).Should().BeTrue();
            releaseRefresh.TrySetResult();
            await refreshReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await advance.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Yield();
            tokens.CurrentToken.Should().Be("new-access");
            store.LoadRefreshToken().Should().Be("new-refresh");
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Late_refresh_rejection_after_new_login_does_not_clear_new_tokens()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authCalls = 0;
        var invoker = new FakeCallInvoker();
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod, _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            return call == 1
                ? LoginResponse(time, "old-access", "old-refresh", TimeSpan.FromSeconds(90))
                : LoginResponse(time, "new-access", "new-refresh", TimeSpan.FromHours(1));
        });
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            refreshStarted.TrySetResult();
            releaseRefresh.Task.GetAwaiter().GetResult();
            refreshRejected.TrySetResult();
            throw FakeCallInvoker.BusinessError(ServerErrors.InvalidRefreshToken);
        });
        var store = new TokenStore(temp.Path("tokens"), "server");
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        try
        {
            (await tokens.LoginAsync("old", "password", null)).Should().BeTrue();
            var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(30)));
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (await tokens.LoginAsync("new", "password", null)).Should().BeTrue();
            releaseRefresh.TrySetResult();
            await refreshRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await advance.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Yield();

            tokens.CurrentToken.Should().Be("new-access");
            store.LoadRefreshToken().Should().Be("new-refresh");
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Late_restore_success_after_logout_does_not_restore_or_persist_tokens()
    {
        using var temp = new TestDirectory();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("old-refresh");
        var time = NewTime();
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            restoreStarted.TrySetResult();
            releaseRestore.Task.GetAwaiter().GetResult();
            return CreateResponse(time, "late-access", TimeSpan.FromHours(1));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        var restore = StartLongRunning(() => tokens.TryRestoreAsync().GetAwaiter().GetResult());
        await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tokens.Logout();
        releaseRestore.TrySetResult();
        await restore.WaitAsync(TimeSpan.FromSeconds(5));

        tokens.CurrentToken.Should().BeNull();
        tokens.IsAuthenticated.Should().BeFalse();
        store.LoadRefreshToken().Should().BeNull();
    }

    [Fact]
    public async Task Late_refresh_success_after_logout_does_not_restore_or_persist_tokens()
    {
        using var temp = new TestDirectory();
        var time = NewTime();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoker = new FakeCallInvoker();
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod,
            _ => LoginResponse(time, "old-access", "old-refresh", TimeSpan.FromSeconds(90)));
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            refreshStarted.TrySetResult();
            releaseRefresh.Task.GetAwaiter().GetResult();
            return CreateResponse(time, "late-refresh-access", TimeSpan.FromHours(1));
        });
        var store = new TokenStore(temp.Path("tokens"), "server");
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);

        try
        {
            (await tokens.LoginAsync("old", "password", null)).Should().BeTrue();
            var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(30)));
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tokens.Logout();
            releaseRefresh.TrySetResult();
            await advance.WaitAsync(TimeSpan.FromSeconds(5));

            tokens.CurrentToken.Should().BeNull();
            tokens.IsAuthenticated.Should().BeFalse();
            store.LoadRefreshToken().Should().BeNull();
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Restore_retry_backoff_grows_to_five_minutes_and_stays_capped()
    {
        using var temp = new TestDirectory();
        var time = new RecordingTimeProvider();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("saved-refresh");
        var invoker = new FakeCallInvoker();
        var attempts = Enumerable.Range(2, 7).ToDictionary(
            attempt => attempt,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var calls = 0;
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            var attempt = Interlocked.Increment(ref calls);
            attempts.GetValueOrDefault(attempt)?.TrySetResult();
            throw new RpcException(new Status(StatusCode.Unavailable, "offline"));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);
        var expectedDelays = new[] { 5, 10, 20, 40, 80, 160, 300 };

        try
        {
            await tokens.TryRestoreAsync();
            calls.Should().Be(1);
            foreach (var (seconds, attempt) in expectedDelays.Select((seconds, index) => (seconds, index + 2)))
            {
                (await time.NextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                    .Should().Be(TimeSpan.FromSeconds(seconds));
                var advance = StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(seconds)));
                await attempts[attempt].Task.WaitAsync(TimeSpan.FromSeconds(5));
                await advance.WaitAsync(TimeSpan.FromSeconds(5));
            }

            (await time.NextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(TimeSpan.FromMinutes(5));
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Fact]
    public async Task Invalid_refresh_token_error_code_clears_saved_token()
    {
        using var temp = new TestDirectory();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("invalid-refresh");
        var invoker = new FakeCallInvoker();
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod,
            _ => throw FakeCallInvoker.BusinessError(ServerErrors.InvalidRefreshToken));
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, NewTime());

        await tokens.TryRestoreAsync();

        store.LoadRefreshToken().Should().BeNull();
        tokens.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task Failed_precondition_detail_without_error_trailer_keeps_saved_token()
    {
        using var temp = new TestDirectory();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("saved-refresh");
        var invoker = new FakeCallInvoker();
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod,
            _ => throw new RpcException(new Status(StatusCode.FailedPrecondition, ServerErrors.InvalidRefreshToken)));
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, NewTime());

        try
        {
            await tokens.TryRestoreAsync();

            store.LoadRefreshToken().Should().Be("saved-refresh");
            tokens.IsAuthenticated.Should().BeFalse();
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_without_restore_cancellation_keeps_token_and_retries(bool operationCancelled)
    {
        using var temp = new TestDirectory();
        var time = new RecordingTimeProvider();
        var store = new TokenStore(temp.Path("tokens"), "server");
        store.SaveRefreshToken("saved-refresh");
        var invoker = new FakeCallInvoker();
        var calls = 0;
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            if (++calls == 1)
                throw operationCancelled ? new OperationCanceledException()
                    : new RpcException(new Status(StatusCode.Cancelled, "transport cancelled"));
            return CreateResponse(time, "restored-access", TimeSpan.FromHours(1));
        });
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker), store, time);
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tokens.SessionRestored += () =>
        {
            restored.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await tokens.TryRestoreAsync();
            store.LoadRefreshToken().Should().Be("saved-refresh");
            (await time.NextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(TimeSpan.FromSeconds(5));
            await StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(5))).WaitAsync(TimeSpan.FromSeconds(5));
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            tokens.CurrentToken.Should().Be("restored-access");
            calls.Should().Be(2);
        }
        finally
        {
            tokens.Stop();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_refresh_cancellation_stops_without_retry(bool operationCancelled)
    {
        using var temp = new TestDirectory();
        var time = new RecordingTimeProvider();
        var invoker = new FakeCallInvoker();
        var tokens = new TokenManager(new IdentityApi.IdentityApiClient(invoker),
            new TokenStore(temp.Path("tokens"), "server"), time);
        var calls = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        invoker.Handle<AuthRequest, AuthResponse>(AuthMethod, _ =>
            LoginResponse(time, "access", "refresh", TimeSpan.FromSeconds(90)));
        invoker.Handle<CreateTokenRequest, CreateTokenResponse>(CreateTokenMethod, _ =>
        {
            ++calls;
            tokens.BeginLogout();
            cancelled.TrySetResult();
            throw operationCancelled ? new OperationCanceledException()
                : new RpcException(new Status(StatusCode.Cancelled, "own cancellation"));
        });
        try
        {
            (await tokens.LoginAsync("user", "password", null)).Should().BeTrue();
            (await time.NextDelayAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().Be(TimeSpan.FromSeconds(30));
            await StartLongRunning(() => time.Advance(TimeSpan.FromSeconds(30))).WaitAsync(TimeSpan.FromSeconds(5));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromMinutes(10));
            calls.Should().Be(1);
            tokens.CurrentToken.Should().Be("access");
        }
        finally
        {
            tokens.Stop();
        }
    }

    private static FakeTimeProvider NewTime()
        => new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    private sealed class RetryObservedTimeProvider : FakeTimeProvider
    {
        public RetryObservedTimeProvider()
            : base(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero))
        {
        }

        public TaskCompletionSource RetryDelayScheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(5))
                RetryDelayScheduled.TrySetResult();
            return timer;
        }
    }

    private sealed class RecordingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _scheduledDelays = Channel.CreateUnbounded<TimeSpan>();

        public RecordingTimeProvider()
            : base(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero))
        {
        }

        public ValueTask<TimeSpan> NextDelayAsync() => _scheduledDelays.Reader.ReadAsync();

        public override ITimer CreateTimer(TimerCallback callback, object? state,
            TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime != Timeout.InfiniteTimeSpan)
                _scheduledDelays.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private static Task<T> StartLongRunning<T>(Func<T> action)
        => Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static Task StartLongRunning(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static AuthResponse LoginResponse(FakeTimeProvider time, string access, string refresh, TimeSpan lifetime)
        => new()
        {
            AccessToken = new Token
            {
                Value = access,
                ExpirationDate = Timestamp.FromDateTime(time.GetUtcNow().UtcDateTime.Add(lifetime)),
            },
            RefreshToken = new Token { Value = refresh },
        };

    private static CreateTokenResponse CreateResponse(FakeTimeProvider time, string access, TimeSpan lifetime)
        => new()
        {
            AccessToken = new Token
            {
                Value = access,
                ExpirationDate = Timestamp.FromDateTime(time.GetUtcNow().UtcDateTime.Add(lifetime)),
            },
        };
}

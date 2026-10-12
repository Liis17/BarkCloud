using System.Collections.Concurrent;
using System.Security.Authentication;

using BarkCloud.Drive.Contracts.Localization;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Files;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;

using DokanNet;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class DriveEngineSessionTests
{
    [Fact]
    public async Task Login_tls_failure_returns_tls_guidance()
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.FailNext("/barkcloud.identity.IdentityApi/Auth",
            new RpcException(new Status(StatusCode.Unavailable, "service unavailable",
                new AuthenticationException("certificate rejected"))));

        var status = await harness.Engine.LoginAsync("user", "password", null);

        status.Error.Should().Be(Loc.T("Eng_TlsConnectionError"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Begin_web_authn_returns_error_only_for_tls_failures(bool tlsFailure)
    {
        await using var harness = new EngineHarness();
        var exception = tlsFailure
            ? new RpcException(new Status(StatusCode.Unavailable, "service unavailable",
                new AuthenticationException("certificate rejected")))
            : new RpcException(new Status(StatusCode.NotFound, "no security keys"));
        harness.IdentityInvoker.FailNext("/barkcloud.identity.IdentityApi/BeginWebAuthnAssertion", exception);

        var challenge = await harness.Engine.BeginWebAuthnAsync();

        challenge.Error.Should().Be(tlsFailure ? Loc.T("Eng_TlsConnectionError") : null);
        if (!tlsFailure)
        {
            challenge.OptionsJson.Should().BeNullOrEmpty();
            challenge.ChallengeId.Should().BeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Mounted_login_closes_old_account_handles_before_changing_token(bool placeholder, bool webAuthn)
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
            LoginResponse(harness.Time, $"access-{++authCalls}", $"refresh-{authCalls}"));
        harness.IdentityInvoker.Handle<CompleteWebAuthnAssertionRequest, AuthResponse>(
            "/barkcloud.identity.IdentityApi/CompleteWebAuthnAssertion", _ =>
            LoginResponse(harness.Time, $"access-{++authCalls}", $"refresh-{authCalls}"));
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
            new GetUserResponse { User = new User { Id = authCalls, Username = $"user-{authCalls}" } });
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        var file = harness.RegisterPlaceholder("old-data.txt", openCount: 1, contents: [4, 5, 6], modified: true);
        file.Kind = placeholder ? LocalFileKind.Placeholder : LocalFileKind.Writable;
        file.ActiveCount = 1;
        file.Hydrated = true;
        var info = new FakeDokanFileInfo { Context = new HandleContext { File = file } };
        var uploadTokens = new List<string?>();
        harness.Cloud.Invoker.OnCall = method =>
        {
            if (method == FakeCloudMethods.CreateUploadSession)
                uploadTokens.Add(harness.Tokens.CurrentToken);
        };
        void CloseHandle()
        {
            harness.FileSystem.Cleanup(file.Path, info);
            harness.FileSystem.CloseFile(file.Path, info);
        }
        harness.BeforeUnmount = CloseHandle;

        var status = webAuthn
            ? await harness.Engine.CompleteWebAuthnAsync("challenge", "assertion")
            : await harness.Engine.LoginAsync("second", "password", null);
        status.Error.Should().BeNull();
        // Полный цикл Dokany выполняется при unmount; на прежнем коде хэндл остаётся открытым.
        if (info.Context is not null)
            CloseHandle();

        uploadTokens.Should().Equal("access-1");
        var uploadPart = harness.Cloud.HttpHandler.Calls.Should().ContainSingle().Subject;
        uploadPart.Method.Should().Be("PUT");
        uploadPart.Headers.Should().ContainKey("X-Upload-Token").And.NotContainKey("x-auth-token");
        harness.Tokens.CurrentToken.Should().Be("access-2");
        harness.IsMounted.Should().BeTrue();
    }

    [Fact]
    public async Task New_login_flushes_old_account_deletes_before_identity_auth()
    {
        await using var harness = new EngineHarness();
        var order = new ConcurrentQueue<string>();
        var authCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            if (Interlocked.Increment(ref authCalls) == 2)
                order.Enqueue("new-auth");
            return LoginResponse(harness.Time, "access-" + authCalls, "refresh-" + authCalls);
        });
        harness.Cloud.Invoker.OnCall = method =>
        {
            if (method == FakeCloudMethods.DeleteFileEntries)
                order.Enqueue("flush-pending");
        };
        harness.Cloud.Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries,
            _ => throw new RpcException(new Status(StatusCode.Unavailable, "offline")));
        (await harness.Tokens.LoginAsync("old", "password", null)).Should().BeTrue();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        var placeholder = harness.RegisterPlaceholder("old-draft.txt");
        harness.BeforeUnmount = () => order.Enqueue("unmount");
        harness.BeforeMount = () =>
        {
            File.Exists(placeholder.TempPath).Should().BeFalse();
            order.Enqueue("mount-after-cleanup");
        };
        harness.Gateway.QueueDeleteFileEntry("old-entry", "");

        var status = await harness.Engine.LoginAsync("new", "password", null);

        status.Error.Should().BeNull();
        order.Should().ContainInOrder("flush-pending", "unmount", "new-auth", "mount-after-cleanup");
        status.Mounted.Should().BeTrue();
        harness.Tokens.CurrentToken.Should().Be("access-2");
        harness.FileSystem.LastSyncError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Mounted_login_flushes_deletes_queued_during_unmount_with_old_token()
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            return LoginResponse(harness.Time, $"access-{call}", $"refresh-{call}");
        });
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
            new GetUserResponse { User = new User { Id = authCalls } });
        var deleteTokens = new List<string?>();
        harness.Cloud.Invoker.OnCall = method =>
        {
            if (method == FakeCloudMethods.DeleteFileEntries)
                deleteTokens.Add(harness.Tokens.CurrentToken);
        };
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        harness.BeforeUnmount = () => harness.Gateway.QueueDeleteFileEntry("cleanup-entry", "");

        var status = await harness.Engine.LoginAsync("second", "password", null);
        harness.Time.Advance(TimeSpan.FromSeconds(30));

        status.Error.Should().BeNull();
        deleteTokens.Should().Equal("access-1");
    }

    [Fact]
    public async Task Mounted_login_after_rejection_drops_old_deletes_before_authentication()
    {
        await using var harness = new EngineHarness();
        var queuedFile = harness.Cloud.AddFile("", "queued.txt");
        var cleanupFile = harness.Cloud.AddFile("", "cleanup.txt");
        var authCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            return LoginResponse(harness.Time, $"access-{call}", $"refresh-{call}");
        });
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
            new GetUserResponse { User = new User { Id = authCalls } });
        var deleteTokens = new List<string?>();
        harness.Cloud.Invoker.OnCall = method =>
        {
            if (method == FakeCloudMethods.DeleteFileEntries)
                deleteTokens.Add(harness.Tokens.CurrentToken);
        };
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        harness.Gateway.ListDirectory("");
        harness.Gateway.QueueDeleteFileEntry(queuedFile.EntryId, "");
        harness.Tokens.Logout(); // Сессию отклонил сервер.
        harness.BeforeUnmount = () => harness.Gateway.QueueDeleteFileEntry(cleanupFile.EntryId, "");

        var status = await harness.Engine.LoginAsync("second", "password", null);

        status.Error.Should().BeNull();
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        deleteTokens.Should().BeEmpty();
        status.LastSyncError.Should().NotBeNullOrWhiteSpace();
        var listing = harness.Gateway.ListDirectory("");
        listing.Files.Should().Contain(file => file.Entry.Id == queuedFile.EntryId);
        listing.Files.Should().Contain(file => file.Entry.Id == cleanupFile.EntryId);
    }

    [Fact]
    public async Task Unmount_waits_for_login_and_unmounts_after_session_switch()
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        var secondAuthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondAuth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            if (call == 2)
            {
                secondAuthStarted.TrySetResult();
                releaseSecondAuth.Task.GetAwaiter().GetResult();
            }
            return LoginResponse(harness.Time, $"access-{call}", $"refresh-{call}");
        });
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
            new GetUserResponse { User = new User { Id = authCalls } });
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();

        var login = Task.Factory.StartNew(
            () => harness.Engine.LoginAsync("second", "password", null).GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await secondAuthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unmount = harness.Engine.UnmountAsync();

        unmount.IsCompleted.Should().BeFalse();
        releaseSecondAuth.TrySetResult();
        (await login.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().BeNull();
        (await unmount.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().BeNull();
        harness.IsMounted.Should().BeFalse();
    }

    [Fact]
    public async Task Logout_flushes_pending_deletes_before_identity_logout()
    {
        await using var harness = new EngineHarness();
        var order = new ConcurrentQueue<string>();
        var deleteCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth",
            _ => LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout",
            _ => new LogoutResponse());
        harness.IdentityInvoker.OnCall = method =>
        {
            if (method == "/barkcloud.identity.IdentityApi/Logout")
                order.Enqueue("identity-logout");
        };
        harness.Cloud.Invoker.OnCall = method =>
        {
            if (method == FakeCloudMethods.DeleteFileEntries)
                order.Enqueue(Interlocked.Increment(ref deleteCalls) == 1
                    ? "flush-before-unmount"
                    : "flush-after-unmount");
        };
        harness.BeforeUnmount = () =>
        {
            order.Enqueue("unmount");
            harness.Gateway.QueueDeleteFileEntry("cleanup-entry", "");
        };
        (await harness.Tokens.LoginAsync("user", "password", null)).Should().BeTrue();
        harness.Gateway.ListDirectory("");
        harness.Gateway.QueueDeleteFileEntry("queued-entry", "");

        var status = await harness.Engine.LogoutAsync();

        status.Error.Should().BeNull();
        order.Should().ContainInOrder("flush-before-unmount", "unmount", "flush-after-unmount", "identity-logout");
        harness.Tokens.IsAuthenticated.Should().BeFalse();

        harness.Gateway.ListDirectory("");
        harness.Cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.ListDirectoryDetailed)
            .Should().Be(2);
    }

    [Fact]
    public async Task Logout_drops_undelivered_deletes_and_clears_the_local_session()
    {
        await using var harness = new EngineHarness();
        var file = harness.Cloud.AddFile("", "queued.txt");
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth",
            _ => LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout",
            _ => new LogoutResponse());
        harness.Cloud.Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries,
            _ => throw new RpcException(new Status(StatusCode.Unavailable, "offline")));
        (await harness.Tokens.LoginAsync("user", "password", null)).Should().BeTrue();
        harness.Gateway.QueueDeleteFileEntry(file.EntryId, "");

        var status = await harness.Engine.LogoutAsync();

        status.Error.Should().BeNull();
        status.Authenticated.Should().BeFalse();
        status.LastSyncError.Should().NotBeNullOrWhiteSpace();
        harness.TokenStore.LoadRefreshToken().Should().BeNull();
        harness.Gateway.ListDirectory("").Files.Should().Contain(entry => entry.Entry.Id == file.EntryId);
    }

    [Fact]
    public async Task Shutdown_drops_failed_deletes_within_budget_and_sets_last_sync_error()
    {
        await using var harness = new EngineHarness();
        harness.Cloud.Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries,
            _ => throw new RpcException(new Status(StatusCode.Unavailable, "offline")));
        harness.Gateway.QueueDeleteFileEntry("queued-entry", "");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        await harness.Engine.ShutdownAsync();

        System.Diagnostics.Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        harness.FileSystem.LastSyncError.Should().NotBeNullOrWhiteSpace();
        harness.Cloud.Invoker.Calls.Count(call => call.MethodFullName == FakeCloudMethods.DeleteFileEntries)
            .Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_had_session_login_keeps_refresh_and_restarts_worker_after_guarded_switch(bool webAuthn)
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        var secondAuthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondAuth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AuthResponse Authenticate()
        {
            if (Interlocked.Increment(ref authCalls) == 1)
                return LoginResponse(harness.Time, "old-access", "old-refresh", TimeSpan.FromSeconds(90));

            secondAuthStarted.TrySetResult();
            releaseSecondAuth.Task.GetAwaiter().GetResult();
            throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid password"));
        }
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ => Authenticate());
        harness.IdentityInvoker.Handle<CompleteWebAuthnAssertionRequest, AuthResponse>(
            "/barkcloud.identity.IdentityApi/CompleteWebAuthnAssertion", _ => Authenticate());
        harness.IdentityInvoker.Handle<CreateTokenRequest, CreateTokenResponse>(
            "/barkcloud.identity.IdentityApi/CreateToken", _ =>
            {
                refreshStarted.TrySetResult();
                return new CreateTokenResponse
                {
                    AccessToken = new Token
                    {
                        Value = "refreshed-access",
                        ExpirationDate = Timestamp.FromDateTime(
                            harness.Time.GetUtcNow().UtcDateTime.AddHours(1)),
                    },
                };
            });
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser",
            _ => new GetUserResponse { User = new User { Id = 42, Username = "old-user" } });
        (await harness.Engine.LoginAsync("old", "password", null)).Error.Should().BeNull();
        harness.OutboxWorker.IsRunning.Should().BeTrue();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();

        var login = Task.Factory.StartNew(
            () => (webAuthn ? harness.Engine.CompleteWebAuthnAsync("challenge", "assertion")
                : harness.Engine.LoginAsync("new", "wrong-password", null)).GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await secondAuthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.OutboxWorker.IsRunning.Should().BeFalse();
        await harness.Engine.GetStatusAsync();
        harness.OutboxWorker.IsRunning.Should().BeFalse();
        releaseSecondAuth.TrySetResult();

        var failedStatus = await login.WaitAsync(TimeSpan.FromSeconds(5));
        failedStatus.Error.Should().Contain("invalid password");
        harness.IsMounted.Should().BeTrue();
        harness.DriveLetter.Should().Be("Z");
        harness.Tokens.CurrentToken.Should().Be("old-access");
        harness.OutboxWorker.IsRunning.Should().BeTrue();

        var advance = Task.Factory.StartNew(() => harness.Time.Advance(TimeSpan.FromSeconds(30)),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await advance.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Tokens.CurrentToken.Should().Be("refreshed-access");
    }

    [Fact]
    public async Task Switching_accounts_removes_closed_placeholder_files()
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        var userCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            return LoginResponse(harness.Time, $"access-{call}", $"refresh-{call}");
        });
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
        {
            var call = Interlocked.Increment(ref userCalls);
            return new GetUserResponse { User = new User { Id = call, Username = $"user-{call}" } };
        });
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        var placeholder = harness.RegisterPlaceholder("draft.txt");
        harness.FileSystem.GetFileInformation("\\draft.txt", out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);

        (await harness.Engine.LoginAsync("second", "password", null)).Error.Should().BeNull();

        harness.FileSystem.GetFileInformation("\\draft.txt", out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.FileNotFound);
        File.Exists(placeholder.TempPath).Should().BeFalse();
    }

    [Fact]
    public async Task Login_after_session_rejection_removes_placeholders_for_the_previous_user()
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        var userCalls = 0;
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            return LoginResponse(harness.Time, $"access-{call}", $"refresh-{call}");
        });
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
        {
            var call = Interlocked.Increment(ref userCalls);
            return new GetUserResponse { User = new User { Id = call, Username = $"user-{call}" } };
        });
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        var placeholder = harness.RegisterPlaceholder("rejected-session-draft.txt");
        harness.Tokens.Logout();

        var status = await harness.Engine.LoginAsync("second", "password", null);

        status.Error.Should().BeNull();
        harness.FileSystem.GetFileInformation("\\rejected-session-draft.txt", out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.FileNotFound);
        File.Exists(placeholder.TempPath).Should().BeFalse();
    }

    [Fact]
    public async Task Mount_and_remount_are_rejected_while_logout_is_in_progress()
    {
        await using var harness = new EngineHarness();
        var logoutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth",
            _ => LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout", _ =>
        {
            logoutStarted.TrySetResult();
            releaseLogout.Task.GetAwaiter().GetResult();
            return new LogoutResponse();
        });
        (await harness.Engine.LoginAsync("user", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        var logout = Task.Factory.StartNew(
            () => harness.Engine.LogoutAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await logoutStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var mountTask = harness.Engine.MountAsync("Z", null);
        var remountTask = harness.Engine.RemountAsync("X", null);

        mountTask.IsCompleted.Should().BeFalse();
        remountTask.IsCompleted.Should().BeFalse();

        harness.Tokens.IsAuthenticated.Should().BeTrue();
        harness.IsMounted.Should().BeFalse();
        releaseLogout.TrySetResult();
        (await logout.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().BeNull();
        (await mountTask.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().Be(Loc.T("Eng_LoginFirst"));
        (await remountTask.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().Be(Loc.T("Eng_LoginFirst"));
        harness.IsMounted.Should().BeFalse();
    }

    [Fact]
    public async Task Logout_preserves_and_reports_placeholder_data()
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth",
            _ => LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout",
            _ => new LogoutResponse());
        (await harness.Engine.LoginAsync("user", "password", null)).Error.Should().BeNull();
        var placeholder = harness.RegisterPlaceholder("unsaved-draft.txt", contents: [1, 2, 3], modified: true);

        var status = await harness.Engine.LogoutAsync();

        status.Error.Should().BeNull();
        File.ReadAllBytes(placeholder.TempPath).Should().Equal([1, 2, 3]);
        harness.FileSystem.GetFileInformation("\\unsaved-draft.txt", out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.FileNotFound);
        status.LastSyncError.Should().Contain("unsaved-draft.txt").And.Contain(placeholder.TempPath);

        (await harness.Engine.LoginAsync("user", "password", null)).Error.Should().BeNull();
        var saved = harness.RegisterPlaceholder("saved.txt", openCount: 1, contents: [9], modified: true);
        saved.Kind = LocalFileKind.Writable;
        saved.ActiveCount = 1;
        saved.Hydrated = true;
        var info = new FakeDokanFileInfo { Context = new HandleContext { File = saved } };
        harness.FileSystem.Cleanup(saved.Path, info);
        harness.FileSystem.CloseFile(saved.Path, info);
        harness.FileSystem.LastSyncError.Should().Contain(placeholder.TempPath);
    }

    [Fact]
    public async Task Logout_preserves_placeholder_marked_keep_data_even_when_empty()
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth",
            _ => LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout",
            _ => new LogoutResponse());
        (await harness.Engine.LoginAsync("user", "password", null)).Error.Should().BeNull();
        var placeholder = harness.RegisterPlaceholder("retry-draft.txt", keepData: true);

        var status = await harness.Engine.LogoutAsync();

        status.Error.Should().BeNull();
        File.Exists(placeholder.TempPath).Should().BeTrue();
        status.LastSyncError.Should().Contain("retry-draft.txt");
    }

    [Fact]
    public async Task Get_status_does_not_restart_outbox_after_logout_wins_profile_race()
    {
        await using var harness = new EngineHarness();
        var getUserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGetUser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
        {
            getUserStarted.TrySetResult();
            releaseGetUser.Task.GetAwaiter().GetResult();
            return new GetUserResponse { User = new User { Id = 42, Username = "late-user" } };
        });
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth",
            _ => LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout",
            _ => new LogoutResponse());
        (await harness.Tokens.LoginAsync("user", "password", null)).Should().BeTrue();

        var statusTask = Task.Factory.StartNew(() => harness.Engine.GetStatusAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await getUserStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = await harness.Engine.LogoutAsync();
        logout.Error.Should().BeNull();
        releaseGetUser.TrySetResult();

        var status = await statusTask.WaitAsync(TimeSpan.FromSeconds(5));
        status.Authenticated.Should().BeFalse();
        harness.Profile.UserId.Should().BeNull();
        harness.OutboxWorker.IsRunning.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Mounted_login_after_rejection_preserves_old_owner_in_outbox(bool placeholder, bool webAuthn)
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", request =>
            LoginResponse(harness.Time, request.Username + "-access", request.Username + "-refresh"));
        harness.IdentityInvoker.Handle<CompleteWebAuthnAssertionRequest, AuthResponse>(
            "/barkcloud.identity.IdentityApi/CompleteWebAuthnAssertion", _ =>
            LoginResponse(harness.Time, "second-access", "second-refresh"));
        var userCalls = 0;
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
            new GetUserResponse { User = new User { Id = ++userCalls } });
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        var file = harness.RegisterPlaceholder("old-data.txt", openCount: 1, contents: [7, 8, 9], modified: true);
        file.Kind = placeholder ? LocalFileKind.Placeholder : LocalFileKind.Writable;
        file.ActiveCount = 1;
        file.Hydrated = true;
        var info = new FakeDokanFileInfo { Context = new HandleContext { File = file } };
        harness.Tokens.Logout(); // Сервер отклонил сессию; профиль и LastUserId ещё известны.
        harness.Cloud.Invoker.Handle<CreateUploadSessionRequest, UploadSessionResponse>(
            FakeCloudMethods.CreateUploadSession, _ =>
        {
            harness.Tokens.CurrentToken.Should().BeNull();
            throw new RpcException(new Status(StatusCode.Unauthenticated, "rejected"));
        });
        harness.BeforeUnmount = () =>
        {
            harness.OutboxWorker.IsRunning.Should().BeFalse();
            harness.FileSystem.Cleanup(file.Path, info);
            harness.FileSystem.CloseFile(file.Path, info);
        };

        var status = webAuthn
            ? await harness.Engine.CompleteWebAuthnAsync("challenge", "assertion")
            : await harness.Engine.LoginAsync("second", "password", null);

        status.Error.Should().BeNull();
        status.Mounted.Should().BeTrue();
        status.DriveLetter.Should().Be("Z");
        info.Context.Should().BeNull();
        var entry = harness.Outbox.Entries.Should().ContainSingle().Subject;
        entry.OwnerUserId.Should().Be("1");
        entry.ServerKey.Should().Be("server");
        File.ReadAllBytes(entry.DataPath).Should().Equal([7, 8, 9]);
        harness.Cloud.Invoker.Calls.Where(call => call.MethodFullName == FakeCloudMethods.CreateUploadSession)
            .Should().HaveCount(2); // Cleanup и повтор при последнем CloseFile, оба до авторизации.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_user_login_remounts_without_removing_placeholders(bool webAuthn)
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
            LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<CompleteWebAuthnAssertionRequest, AuthResponse>(
            "/barkcloud.identity.IdentityApi/CompleteWebAuthnAssertion", _ =>
            LoginResponse(harness.Time, "key-access", "key-refresh"));
        harness.UsersInvoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser", _ =>
            new GetUserResponse { User = new User { Id = 42 } });
        (await harness.Engine.LoginAsync("user", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", "Label")).Error.Should().BeNull();
        var placeholder = harness.RegisterPlaceholder("draft.txt");
        var order = new List<string>();
        harness.BeforeUnmount = () => order.Add("unmount");
        harness.BeforeMount = () => order.Add("mount");

        var status = webAuthn
            ? await harness.Engine.CompleteWebAuthnAsync("challenge", "assertion")
            : await harness.Engine.LoginAsync("user", "password", null);

        status.Error.Should().BeNull();
        status.Mounted.Should().BeTrue();
        status.DriveLetter.Should().Be("Z");
        order.Should().Equal("unmount", "mount");
        harness.FileSystem.VolumeLabel.Should().Be("Label");
        File.Exists(placeholder.TempPath).Should().BeTrue();
        harness.FileSystem.GetFileInformation(placeholder.Path, out _, new FakeDokanFileInfo())
            .Should().Be(DokanResult.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_waits_for_mount_already_in_progress_and_leaves_disk_unmounted(bool remount)
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
            LoginResponse(harness.Time, "access", "refresh"));
        harness.IdentityInvoker.Handle<LogoutRequest, LogoutResponse>("/barkcloud.identity.IdentityApi/Logout", _ =>
            new LogoutResponse());
        (await harness.Engine.LoginAsync("user", "password", null)).Error.Should().BeNull();
        if (remount)
            (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();
        var mountStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeMount = () =>
        {
            mountStarted.TrySetResult();
            releaseMount.Task.GetAwaiter().GetResult();
        };
        var mounting = Task.Factory.StartNew(
            () => (remount ? harness.Engine.RemountAsync("X", null) : harness.Engine.MountAsync("Z", null))
                .GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await mountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = harness.Engine.LogoutAsync();
        try
        {
            logout.IsCompleted.Should().BeFalse();
        }
        finally
        {
            releaseMount.TrySetResult();
        }

        (await mounting.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().BeNull();
        (await logout.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().BeNull();
        harness.IsMounted.Should().BeFalse();
    }

    [Fact]
    public async Task Login_finishing_during_shutdown_does_not_restart_or_mount()
    {
        await using var harness = new EngineHarness();
        var authCalls = 0;
        var secondAuthStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondAuth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
        {
            var call = Interlocked.Increment(ref authCalls);
            if (call == 2)
            {
                secondAuthStarted.TrySetResult();
                releaseSecondAuth.Task.GetAwaiter().GetResult();
            }
            return LoginResponse(harness.Time, $"access-{call}", $"refresh-{call}");
        });
        harness.Cloud.Invoker.Handle<DeleteFileEntriesRequest, DeleteFileEntriesResponse>(
            FakeCloudMethods.DeleteFileEntries, _ =>
            {
                deleteStarted.TrySetResult();
                releaseDelete.Task.GetAwaiter().GetResult();
                return new DeleteFileEntriesResponse();
            });
        (await harness.Engine.LoginAsync("first", "password", null)).Error.Should().BeNull();
        (await harness.Engine.MountAsync("Z", null)).Error.Should().BeNull();

        var login = Task.Factory.StartNew(
            () => harness.Engine.LoginAsync("second", "password", null).GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await secondAuthStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mounting = harness.Engine.MountAsync("Y", null);
        var remounting = harness.Engine.RemountAsync("X", null);
        harness.Gateway.QueueDeleteFileEntry("queued-entry", "");

        var shutdown = Task.Factory.StartNew(
            () => harness.Engine.ShutdownAsync().GetAwaiter().GetResult(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseSecondAuth.TrySetResult();

            await login.WaitAsync(TimeSpan.FromSeconds(5));
            (await mounting.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().Be(Loc.T("Common_EngineNotRunning"));
            (await remounting.WaitAsync(TimeSpan.FromSeconds(5))).Error.Should().Be(Loc.T("Common_EngineNotRunning"));
            harness.OutboxWorker.IsRunning.Should().BeFalse();
        }
        finally
        {
            releaseSecondAuth.TrySetResult();
            releaseDelete.TrySetResult();
        }

        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        harness.IsMounted.Should().BeFalse();
        harness.OutboxWorker.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Session_restored_auto_mount_completes_without_reentering_session_lock()
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
            LoginResponse(harness.Time, "access", "refresh"));
        harness.Settings.SetLastMount("Z", "Label");
        (await harness.Tokens.LoginAsync("user", "password", null)).Should().BeTrue();

        await harness.Engine.SessionRestoredAsync().WaitAsync(TimeSpan.FromSeconds(5));

        harness.IsMounted.Should().BeTrue();
        harness.DriveLetter.Should().Be("Z");
        harness.OutboxWorker.IsRunning.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_mount_restores_label_before_next_successful_mount(bool remount)
    {
        await using var harness = new EngineHarness();
        harness.IdentityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ =>
            LoginResponse(harness.Time, "access", "refresh"));
        (await harness.Tokens.LoginAsync("user", "password", null)).Should().BeTrue();
        (await harness.Engine.MountAsync("Z", "Confirmed")).Error.Should().BeNull();
        if (!remount)
            await harness.Engine.UnmountAsync();
        harness.BeforeMount = () => throw new IOException("mount failed");

        var failed = remount
            ? await harness.Engine.RemountAsync(null, "Unconfirmed")
            : await harness.Engine.MountAsync("X", "Unconfirmed");

        failed.Error.Should().NotBeNullOrEmpty();
        harness.FileSystem.VolumeLabel.Should().Be("Confirmed");
        harness.Settings.GetLastMount().Should().Be(("Z", "Confirmed"));
        harness.BeforeMount = null;

        var succeeded = remount
            ? await harness.Engine.RemountAsync("X", null)
            : await harness.Engine.MountAsync("X", null);

        succeeded.Error.Should().BeNull();
        succeeded.VolumeLabel.Should().Be("Confirmed");
        harness.Settings.GetLastMount().Should().Be(("X", "Confirmed"));
    }

    private static AuthResponse LoginResponse(FakeTimeProvider time, string access, string refresh,
        TimeSpan? lifetime = null)
        => new()
        {
            AccessToken = new Token
            {
                Value = access,
                ExpirationDate = Timestamp.FromDateTime(time.GetUtcNow().UtcDateTime.Add(lifetime ?? TimeSpan.FromHours(1))),
            },
            RefreshToken = new Token { Value = refresh },
        };

    private sealed class EngineHarness : IAsyncDisposable
    {
        private readonly TestDirectory _temp = new();
        private readonly MountManager _mount = new();
        private readonly CancellationTokenSource _lifetime = new();

        public EngineHarness()
        {
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
            Cloud = new FakeCloud();
            IdentityInvoker = new FakeCallInvoker();
            UsersInvoker = new FakeCallInvoker();
            Settings = new EngineSettingsStore(_temp.Path("settings"));
            Profile = new UserProfile(new UsersApi.UsersApiClient(UsersInvoker),
                userId => Settings.SetLastUserId("server", userId));
            TokenStore = new TokenStore(_temp.Path("tokens"), "server");
            Tokens = new TokenManager(new IdentityApi.IdentityApiClient(IdentityInvoker), TokenStore, Time);
            Gateway = new CloudGateway(new CloudApi.CloudApiClient(Cloud.Invoker),
                new FilesApi.FilesApiClient(Cloud.Invoker),
                new HttpClient(Cloud.HttpHandler, disposeHandler: false), "https://files.test",
                _temp.Path("cache"), Time, uploadOrigin: "https://files.test");
            Outbox = new UploadOutbox(_temp.Path("outbox"), Time);
            FileSystem = new BarkCloudFileSystem(Gateway, _temp.Path("write"), Outbox,
                "server", () => Profile.UserId ?? Settings.GetLastUserId("server"), Time);
            ProcessOutbox = (entry, cancellationToken) => FileSystem.ProcessOutboxEntryAsync(entry, cancellationToken);
            OutboxWorker = new UploadOutboxWorker(Outbox,
                () => ("server", Profile.UserId ?? Settings.GetLastUserId("server")),
                (entry, cancellationToken) => ProcessOutbox(entry, cancellationToken),
                FileSystem.CompleteOutboxEntry,
                time: Time,
                deferDataDeletion: FileSystem.DeferOutboxDataDeletion);
            FileSystem.OutboxAdded = OutboxWorker.Trigger;
            FileSystem.SyncError = FileSystem.SetLastSyncError;
            Engine = new DriveEngine(Tokens, Gateway, _mount, FileSystem, _lifetime,
                Settings, Profile, OutboxWorker, "server", "files.test",
                unmount: () =>
                {
                    BeforeUnmount?.Invoke();
                    DriveLetter = null;
                },
                mountDrive: letter =>
                {
                    BeforeMount?.Invoke();
                    DriveLetter = letter;
                },
                mountedDriveLetter: () => DriveLetter);
        }

        public FakeTimeProvider Time { get; }
        public FakeCloud Cloud { get; }
        public FakeCallInvoker IdentityInvoker { get; }
        public FakeCallInvoker UsersInvoker { get; }
        public EngineSettingsStore Settings { get; }
        public UserProfile Profile { get; }
        public CloudGateway Gateway { get; }
        public UploadOutbox Outbox { get; }
        public BarkCloudFileSystem FileSystem { get; }
        public string? DriveLetter { get; private set; }
        public bool IsMounted => DriveLetter is not null;
        public UploadOutboxWorker OutboxWorker { get; }
        public TokenManager Tokens { get; }
        public TokenStore TokenStore { get; }
        public DriveEngine Engine { get; }
        public Action? BeforeUnmount { get; set; }
        public Action? BeforeMount { get; set; }
        public Func<UploadOutboxEntry, CancellationToken, Task> ProcessOutbox { get; set; }

        public LocalFile RegisterPlaceholder(string name, int openCount = 0, byte[]? contents = null,
            bool modified = false, bool keepData = false)
        {
            var tempPath = _temp.Path("write", name);
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            File.WriteAllBytes(tempPath, contents ?? []);
            var file = new LocalFile
            {
                Path = "\\" + name,
                Name = name,
                DirectoryId = "",
                TempPath = tempPath,
                Kind = LocalFileKind.Placeholder,
                OpenCount = openCount,
                Modified = modified,
                KeepData = keepData,
            };
            FileSystem.RegisterLocalFile(file).Should().BeTrue();
            return file;
        }

        public async ValueTask DisposeAsync()
        {
            Tokens.Stop();
            await OutboxWorker.DisposeAsync();
            Gateway.Dispose();
            _mount.Dispose();
            _lifetime.Dispose();
            _temp.Dispose();
        }
    }
}

using System.Net;

using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Identity;
using BarkCloud.Proto.Users;

using DokanNet;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;

using FileAccess = DokanNet.FileAccess;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class DriveEngineTests
{
    [Fact]
    public async Task Login_clears_previous_owner_when_profile_cannot_load_and_logout_keeps_it_clear()
    {
        using var temp = new TestDirectory();
        var settingsDirectory = temp.Path("settings");
        var settings = new EngineSettingsStore(settingsDirectory);
        settings.SetLastUserId("server", "previous-user");

        var identityInvoker = new FakeCallInvoker();
        identityInvoker.Handle<AuthRequest, AuthResponse>("/barkcloud.identity.IdentityApi/Auth", _ => new AuthResponse
        {
            AccessToken = new Token
            {
                Value = "access-token",
                ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)),
            },
            RefreshToken = new Token { Value = "refresh-token" },
        });
        var userInvoker = new FakeCallInvoker();
        userInvoker.FailNext("/barkcloud.users.UsersApi/GetUser",
            new RpcException(new Status(StatusCode.Unavailable, "профиль недоступен")));
        var profile = new UserProfile(new UsersApi.UsersApiClient(userInvoker),
            userId => settings.SetLastUserId("server", userId));
        var cloud = new FakeCloud();
        cloud.HttpHandler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server",
            () => profile.UserId ?? settings.GetLastUserId("server"));
        await using var worker = new UploadOutboxWorker(outbox,
            () => ("server", profile.UserId ?? settings.GetLastUserId("server")),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            fileSystem.CompleteOutboxEntry,
            time: new FakeTimeProvider(), deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        using var lifetime = new CancellationTokenSource();
        var engine = new DriveEngine(new TokenManager(new IdentityApi.IdentityApiClient(identityInvoker),
                new TokenStore(temp.Path("tokens"))), gateway, new MountManager(), fileSystem, lifetime,
            settings, profile, worker, "server", "files.test");

        var login = await engine.LoginAsync("new-user", "password", null);
        login.Error.Should().BeNull();
        settings.GetLastUserId("server").Should().BeNull();
        new EngineSettingsStore(settingsDirectory).GetLastUserId("server").Should().BeNull();
        profile.UserId.Should().BeNull();

        var info = new FakeDokanFileInfo();
        fileSystem.CreateFile("\\unowned.bin", FileAccess.WriteData, FileShare.ReadWrite, FileMode.CreateNew,
            FileOptions.None, FileAttributes.Normal, info).Should().Be(DokanResult.Success);
        fileSystem.WriteFile("\\unowned.bin", [7], out _, 0, info).Should().Be(DokanResult.Success);
        fileSystem.Cleanup("\\unowned.bin", info);
        fileSystem.CloseFile("\\unowned.bin", info);

        var entry = outbox.Entries.Should().ContainSingle().Subject;
        entry.OwnerUserId.Should().BeNull();
        fileSystem.FindFiles("\\", out var files, new FakeDokanFileInfo()).Should().Be(DokanResult.Success);
        files.Should().BeEmpty();
        await worker.TriggerAsync();
        outbox.Find(entry.Id)!.Attempts.Should().Be(0);

        settings.SetLastUserId("server", "current-user");
        var logout = await engine.LogoutAsync();
        logout.Error.Should().BeNull();
        settings.GetLastUserId("server").Should().BeNull();
        new EngineSettingsStore(settingsDirectory).GetLastUserId("server").Should().BeNull();
    }

    [Fact]
    public async Task Complete_web_authn_clears_previous_owner_before_loading_profile()
    {
        using var temp = new TestDirectory();
        var settingsDirectory = temp.Path("settings");
        var settings = new EngineSettingsStore(settingsDirectory);
        settings.SetLastUserId("server", "previous-user");

        var identityInvoker = new FakeCallInvoker();
        identityInvoker.Handle<CompleteWebAuthnAssertionRequest, AuthResponse>(
            "/barkcloud.identity.IdentityApi/CompleteWebAuthnAssertion", _ => new AuthResponse
            {
                AccessToken = new Token
                {
                    Value = "access-token",
                    ExpirationDate = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(1)),
                },
                RefreshToken = new Token { Value = "refresh-token" },
            });
        var userInvoker = new FakeCallInvoker();
        string? ownerSeenDuringProfileLoad = "not-called";
        userInvoker.OnCall = method =>
        {
            if (method == "/barkcloud.users.UsersApi/GetUser")
                ownerSeenDuringProfileLoad = settings.GetLastUserId("server");
        };
        userInvoker.FailNext("/barkcloud.users.UsersApi/GetUser",
            new RpcException(new Status(StatusCode.Unavailable, "профиль недоступен")));
        var profile = new UserProfile(new UsersApi.UsersApiClient(userInvoker),
            userId => settings.SetLastUserId("server", userId));
        var cloud = new FakeCloud();
        using var gateway = cloud.CreateGateway(temp.Path("cache"));
        var outbox = new UploadOutbox(temp.Path("outbox"));
        var fileSystem = new BarkCloudFileSystem(gateway, temp.Path("write"), outbox, "server",
            () => profile.UserId ?? settings.GetLastUserId("server"));
        await using var worker = new UploadOutboxWorker(outbox,
            () => ("server", profile.UserId ?? settings.GetLastUserId("server")),
            (entry, cancellationToken) => fileSystem.ProcessOutboxEntryAsync(entry, cancellationToken),
            fileSystem.CompleteOutboxEntry,
            time: new FakeTimeProvider(), deferDataDeletion: fileSystem.DeferOutboxDataDeletion);
        using var lifetime = new CancellationTokenSource();
        var engine = new DriveEngine(new TokenManager(new IdentityApi.IdentityApiClient(identityInvoker),
                new TokenStore(temp.Path("tokens"))), gateway, new MountManager(), fileSystem, lifetime,
            settings, profile, worker, "server", "files.test");

        var status = await engine.CompleteWebAuthnAsync("challenge", "assertion");

        status.Error.Should().BeNull();
        ownerSeenDuringProfileLoad.Should().BeNull();
        settings.GetLastUserId("server").Should().BeNull();
        new EngineSettingsStore(settingsDirectory).GetLastUserId("server").Should().BeNull();
    }
}

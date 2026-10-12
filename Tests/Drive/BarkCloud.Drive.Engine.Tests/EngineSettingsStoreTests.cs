using System.Text.Json;

using BarkCloud.Drive.Engine;
using BarkCloud.Drive.Engine.Tests.Fakes;
using BarkCloud.Proto.Users;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class EngineSettingsStoreTests
{
    [Fact]
    public void Default_cache_directory_is_inside_a_custom_settings_directory()
    {
        using var temp = new TestDirectory();
        var store = new EngineSettingsStore(temp.Root);

        store.GetCacheDir().Should().Be(temp.Path("cache"));
    }

    [Fact]
    public void Save_replaces_settings_with_valid_json_and_leaves_no_temporary_file()
    {
        using var temp = new TestDirectory();
        var store = new EngineSettingsStore(temp.Root);

        store.SetCacheDir(temp.Path("cache-next"));

        var settingsPath = temp.Path("settings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
        json.RootElement.GetProperty("CacheDir").GetString().Should().Be(temp.Path("cache-next"));
        Directory.GetFiles(temp.Root, "*.tmp").Should().BeEmpty();
        new EngineSettingsStore(temp.Root).GetCacheDir().Should().Be(temp.Path("cache-next"));
    }

    [Fact]
    public async Task Loading_profile_updates_the_persisted_owner_for_its_server()
    {
        using var temp = new TestDirectory();
        var settings = new EngineSettingsStore(temp.Root);
        settings.SetLastUserId("server", "old-user");
        var invoker = new FakeCallInvoker();
        invoker.Handle<GetUserRequest, GetUserResponse>("/barkcloud.users.UsersApi/GetUser",
            _ => new GetUserResponse { User = new User { Id = 42, Username = "current-user" } });
        var profile = new UserProfile(new UsersApi.UsersApiClient(invoker),
            userId => settings.SetLastUserId("server", userId));
        await profile.EnsureLoadedAsync();
        profile.UserId.Should().Be("42");
        new EngineSettingsStore(temp.Root).GetLastUserId("server").Should().Be("42");
    }
}

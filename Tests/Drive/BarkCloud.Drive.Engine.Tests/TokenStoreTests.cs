using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

namespace BarkCloud.Drive.Engine.Tests;

public sealed class TokenStoreTests
{
    [Fact]
    public void Legacy_refresh_token_is_bound_to_current_server_without_relogin()
    {
        using var temp = new Fakes.TestDirectory();
        var tokenPath = temp.Path("tokens", "refresh.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        var legacy = ProtectedData.Protect(Encoding.UTF8.GetBytes("legacy-refresh"), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(tokenPath, legacy);
        var store = new TokenStore(temp.Path("tokens"), "https://one.test:7020|files:7025|users:7021");

        store.LoadRefreshToken().Should().Be("legacy-refresh");

        var payload = ProtectedData.Unprotect(File.ReadAllBytes(tokenPath), null, DataProtectionScope.CurrentUser);
        using var json = JsonDocument.Parse(payload);
        json.RootElement.GetProperty("ServerKey").GetString()
            .Should().Be("https://one.test:7020|files:7025|users:7021");
        json.RootElement.GetProperty("Token").GetString().Should().Be("legacy-refresh");
    }

    [Fact]
    public void Legacy_refresh_token_is_returned_when_binding_write_is_temporarily_blocked()
    {
        using var temp = new Fakes.TestDirectory();
        var tokenPath = temp.Path("tokens", "refresh.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);
        var legacy = ProtectedData.Protect(Encoding.UTF8.GetBytes("legacy-refresh"), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(tokenPath, legacy);
        var store = new TokenStore(temp.Path("tokens"), "server");

        using (new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            store.LoadRefreshToken().Should().Be("legacy-refresh");

        store.LoadRefreshToken().Should().Be("legacy-refresh");
        var payload = ProtectedData.Unprotect(File.ReadAllBytes(tokenPath), null, DataProtectionScope.CurrentUser);
        using var json = JsonDocument.Parse(payload);
        json.RootElement.GetProperty("Token").GetString().Should().Be("legacy-refresh");
        json.RootElement.GetProperty("ServerKey").GetString().Should().Be("server");
    }

    [Fact]
    public void Changing_server_key_erases_token_without_sending_it()
    {
        using var temp = new Fakes.TestDirectory();
        var directory = temp.Path("tokens");
        new TokenStore(directory, "https://one.test:7020|files:7025|users:7021")
            .SaveRefreshToken("server-one-refresh");
        var otherServer = new TokenStore(directory, "https://one.test:7020|files:7026|users:7021");

        otherServer.LoadRefreshToken().Should().BeNull();
        File.Exists(temp.Path("tokens", "refresh.bin")).Should().BeFalse();
    }
}

using BarkCloud.Builder;

namespace BarkCloud.Builder.Tests;

public sealed class BuilderModelTests
{
    [Fact]
    public void RegenerateServiceSecrets_fills_and_replaces_both_registries()
    {
        var model = new BuilderModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        model.RegenerateServiceSecrets();
        var previous = model.ConfigurationAccessKeysRegistry + model.ServiceCredentialsRegistry;
        model.RegenerateServiceSecrets();

        var keys = model.ConfigurationAccessKeysRegistry.Split(';').Select(v => v.Split('=')[1]).ToArray();
        var credentials = model.ServiceCredentialsRegistry.Split(';').Select(v => v.Split('=')[1]).ToArray();
        Assert.Equal(6, keys.Length);
        Assert.Equal(5, credentials.Length);
        Assert.All(keys, v => Assert.Matches("^[A-Za-z0-9]{50}$", v));
        Assert.All(credentials, v => Assert.Matches("^[A-Za-z0-9]{43}$", v));
        Assert.Equal(11, keys.Concat(credentials).Distinct().Count());
        Assert.NotEqual(previous, model.ConfigurationAccessKeysRegistry + model.ServiceCredentialsRegistry);
        Assert.Contains(nameof(BuilderModel.ConfigurationAccessKeysRegistry), notifications);
        Assert.Contains(nameof(BuilderModel.ServiceCredentialsRegistry), notifications);
        var env = BackendComposeGenerator.BuildEnv(model);
        Assert.All(keys.Concat(credentials), v => Assert.Contains(v, env));
    }
}

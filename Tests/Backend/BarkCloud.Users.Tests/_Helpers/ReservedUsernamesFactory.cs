using BarkCloud.Users.Services;

using Microsoft.Extensions.Configuration;

namespace BarkCloud.Users.Tests._Helpers;

internal static class ReservedUsernamesFactory
{
    public static ReservedUsernamesService Create(params string[] reserved)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReservedNames:Usernames"] = string.Join(",", reserved)
            })
            .Build();

        return new ReservedUsernamesService(config);
    }
}

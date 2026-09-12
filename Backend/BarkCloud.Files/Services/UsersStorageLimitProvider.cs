using BarkCloud.Proto.Users;

namespace BarkCloud.Files.Services;

public sealed class UsersStorageLimitProvider(UsersServerApi.UsersServerApiClient users) : IStorageLimitProvider
{
    public async Task<long?> GetLimitBytesAsync(long ownerId, CancellationToken cancellationToken)
    {
        var response = await users.GetByIdAsync(
            new GetByIdRequest { UserId = ownerId },
            cancellationToken: cancellationToken);
        return response.User.StorageLimitGb > 0
            ? (long)response.User.StorageLimitGb * 1024 * 1024 * 1024
            : null;
    }
}

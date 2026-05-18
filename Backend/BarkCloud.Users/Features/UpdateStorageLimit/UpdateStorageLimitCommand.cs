using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.UpdateStorageLimit;

public class UpdateStorageLimitCommand : IRequest<UpdateStorageLimitResponse>
{
    public long UserId { get; set; }

    public int StorageLimitGb { get; set; }
}

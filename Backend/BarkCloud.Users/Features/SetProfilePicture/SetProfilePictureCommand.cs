using BarkCloud.Proto.Users;

using MediatR;

namespace BarkCloud.Users.Features.SetProfilePicture;

public class SetProfilePictureCommand : IRequest<SetProfilePictureResponse>
{
    public Guid? FileId { get; set; }
}
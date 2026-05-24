namespace BarkCloud.Shared.Exceptions.Files;

public class AlbumNameConflictException : BaseGrpcException
{
    public override string ErrorCode => "B1C3D5E7-9F0A-42B4-86C8-1D3E5F7A9B0C";

    public override string ErrorMessage => "Альбом с таким именем уже существует";
}

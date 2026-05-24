namespace BarkCloud.Shared.Exceptions.Files;

public class AlbumNotFoundException : BaseGrpcException
{
    public override string ErrorCode => "C2D4E6F8-1A3B-45C7-89D0-2E4F6A8B0C1D";

    public override string ErrorMessage => "Альбом не найден";
}

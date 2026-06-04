namespace BarkCloud.Shared.Exceptions.Files;

public class DynamicFolderNotFoundException : BaseGrpcException
{
    public override string ErrorCode => "7A1E9C41-3B2D-4E15-9F86-1C0A7B2D4E61";

    public override string ErrorMessage => "Умная папка не найдена";
}

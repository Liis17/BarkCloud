namespace BarkCloud.Shared.Exceptions.Files;

public class DirectoryNotFoundException : BaseGrpcException
{
    public override string ErrorCode => "B86C8B6E-2A12-44E1-9B6B-1AE0F1C0E1AE";

    public override string ErrorMessage => "Папка не найдена";
}

namespace BarkCloud.Shared.Exceptions.Files;

public class DirectoryTreeCorruptedException : BaseGrpcException
{
    public override string ErrorCode => "7D3E5A91-4C28-4F6B-8E10-5B2A9C4D1F02";

    public override string ErrorMessage => "Структура папок повреждена: обнаружен цикл";
}

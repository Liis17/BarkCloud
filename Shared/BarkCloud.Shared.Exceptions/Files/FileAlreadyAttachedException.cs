namespace BarkCloud.Shared.Exceptions.Files;

public class FileAlreadyAttachedException : BaseGrpcException
{
    public override string ErrorCode => "F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B";

    public override string ErrorMessage => "Файл уже привязан к директории пользователя";
}

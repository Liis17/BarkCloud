namespace BarkCloud.Shared.Exceptions.Files;

public class DirectoryAppearanceInvalidException : BaseGrpcException
{
    public override string ErrorCode => "5D3B8F1E-9A47-4C2B-B6E0-2F7C1A9D4E83";

    public override string ErrorMessage => "Некорректная иконка или цвет папки";
}

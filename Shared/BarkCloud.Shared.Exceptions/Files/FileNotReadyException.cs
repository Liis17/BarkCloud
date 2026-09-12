namespace BarkCloud.Shared.Exceptions.Files;

public sealed class FileNotReadyException : BaseGrpcException
{
    public override string ErrorCode => "4E6B7B02-D071-446A-858D-E97D2F154120";

    public override string ErrorMessage => "Файл ещё обрабатывается";
}

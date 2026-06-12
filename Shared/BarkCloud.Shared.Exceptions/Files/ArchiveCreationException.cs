namespace BarkCloud.Shared.Exceptions.Files;

public class ArchiveCreationException : BaseGrpcException
{
    private readonly string _message;

    public ArchiveCreationException(string message) => _message = message;

    public override string ErrorCode => "F2C9A7B1-3D4E-4F56-9A8B-7C6D5E4F3A2B";

    public override string ErrorMessage => _message;
}

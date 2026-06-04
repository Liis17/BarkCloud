namespace BarkCloud.Shared.Exceptions.Files;

public class DynamicFolderNameConflictException : BaseGrpcException
{
    public override string ErrorCode => "7A1E9C40-3B2D-4E15-9F86-1C0A7B2D4E60";

    public override string ErrorMessage => "Умная папка с таким именем уже существует";
}

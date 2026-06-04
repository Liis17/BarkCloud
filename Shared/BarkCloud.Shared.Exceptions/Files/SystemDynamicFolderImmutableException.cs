namespace BarkCloud.Shared.Exceptions.Files;

public class SystemDynamicFolderImmutableException : BaseGrpcException
{
    public override string ErrorCode => "7A1E9C43-3B2D-4E15-9F86-1C0A7B2D4E63";

    public override string ErrorMessage => "Системную умную папку нельзя изменить или удалить";
}

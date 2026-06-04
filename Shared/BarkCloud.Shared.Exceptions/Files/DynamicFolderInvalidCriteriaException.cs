namespace BarkCloud.Shared.Exceptions.Files;

public class DynamicFolderInvalidCriteriaException : BaseGrpcException
{
    public override string ErrorCode => "7A1E9C42-3B2D-4E15-9F86-1C0A7B2D4E62";

    public override string ErrorMessage => "Некорректные критерии умной папки";
}

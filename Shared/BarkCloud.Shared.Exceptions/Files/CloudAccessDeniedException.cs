namespace BarkCloud.Shared.Exceptions.Files;

public class CloudAccessDeniedException : BaseGrpcException
{
    public override string ErrorCode => "D4F1E2A8-9C5B-4A77-83BB-5E6F7A8B9C01";

    public override string ErrorMessage => "Нет доступа к этому объекту в облаке";
}

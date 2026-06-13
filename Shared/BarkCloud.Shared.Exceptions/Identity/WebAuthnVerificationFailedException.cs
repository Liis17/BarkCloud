namespace BarkCloud.Shared.Exceptions.Identity;

public class WebAuthnVerificationFailedException : BaseGrpcException
{
    public override string ErrorCode => "C9F5E4A3-2D6B-4C8F-A04E-3F7D5B9C1E26";

    public override string ErrorMessage => "Не удалось проверить ключ безопасности";
}

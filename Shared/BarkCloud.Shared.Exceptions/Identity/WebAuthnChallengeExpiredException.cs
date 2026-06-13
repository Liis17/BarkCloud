namespace BarkCloud.Shared.Exceptions.Identity;

public class WebAuthnChallengeExpiredException : BaseGrpcException
{
    public override string ErrorCode => "B8E4D3F2-1C5A-4B7E-9F3D-2E6C4A8B0D15";

    public override string ErrorMessage => "Сессия подтверждения ключа истекла, повторите попытку";
}

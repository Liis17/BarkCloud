namespace BarkCloud.Shared.Exceptions.Identity;

public class NoWebAuthnCredentialsException : BaseGrpcException
{
    public override string ErrorCode => "A7F3C2E1-9B4D-4A6F-8E2C-1D5B3F7A9C04";

    public override string ErrorMessage => "У пользователя нет привязанных ключей безопасности";
}

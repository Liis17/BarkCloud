namespace BarkCloud.Shared.Exceptions.Identity;

public class InvalidPasswordException : BaseGrpcException
{
    public override string ErrorCode => "575B756F-A928-4646-A1C9-0BB6C31BA0D5";

    public override string ErrorMessage => "Неверный пароль";
}

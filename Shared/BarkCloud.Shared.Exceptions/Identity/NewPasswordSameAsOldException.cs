namespace BarkCloud.Shared.Exceptions.Identity;

public class NewPasswordSameAsOldException : BaseGrpcException
{
    public override string ErrorCode => "730737E2-64C9-492B-BE0C-459191C13F76";

    public override string ErrorMessage => "Новый пароль должен отличаться от текущего";
}

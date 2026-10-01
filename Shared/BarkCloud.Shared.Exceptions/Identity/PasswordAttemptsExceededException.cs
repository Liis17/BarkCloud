namespace BarkCloud.Shared.Exceptions.Identity;

public class PasswordAttemptsExceededException : BaseGrpcException
{
    public override string ErrorCode => "3C8E5A17-6D42-4B90-A1F3-7E2B9D0C4A58";

    public override string ErrorMessage => "Слишком много неверных попыток ввода пароля. Повторите позже";
}

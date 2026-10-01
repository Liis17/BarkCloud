namespace BarkCloud.Shared.Exceptions.Identity;

public class NewPasswordRequiredException : BaseGrpcException
{
    public override string ErrorCode => "C806D28E-4F95-4EE7-86A8-8CAB6125730A";

    public override string ErrorMessage => "Укажите новый пароль";
}

namespace BarkCloud.Shared.Exceptions.Identity;

public class RegistrationDisabledException : BaseGrpcException
{
    public override string ErrorCode => "C46C2E13-9838-4935-A88F-D6E0F62F4D23";

    public override string ErrorMessage => "Регистрация новых аккаунтов отключена";
}
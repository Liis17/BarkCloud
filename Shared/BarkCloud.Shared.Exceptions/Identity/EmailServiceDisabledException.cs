namespace BarkCloud.Shared.Exceptions.Identity;

public class EmailServiceDisabledException : BaseGrpcException
{
    public override string ErrorCode => "A1F0C3E2-5B47-4E8A-9C21-7D6F0B2E9A14";

    public override string ErrorMessage => "Почта на сервере не настроена — операция недоступна";
}

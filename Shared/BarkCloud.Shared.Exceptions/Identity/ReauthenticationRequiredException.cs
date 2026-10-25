namespace BarkCloud.Shared.Exceptions.Identity;

public class ReauthenticationRequiredException : BaseGrpcException
{
    public override string ErrorCode => "2C87D75D-F786-4BE8-B928-94C3EA4D7E72";

    public override string ErrorMessage => "Требуется повторная аутентификация";
}

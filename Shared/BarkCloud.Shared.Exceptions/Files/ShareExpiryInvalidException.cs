namespace BarkCloud.Shared.Exceptions.Files;

public class ShareExpiryInvalidException : BaseGrpcException
{
    public override string ErrorCode => "8C1E4A27-3F6B-4D95-A0B2-7E5D9C13F468";

    public override string ErrorMessage => "Срок действия ссылки должен быть в будущем";
}

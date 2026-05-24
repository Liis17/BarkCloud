namespace BarkCloud.Shared.Exceptions.Files;

public class InvalidThumbnailSourceException : BaseGrpcException
{
    public override string ErrorCode => "A7E3D1C9-4B2F-48A6-9C7D-2E1F3A4B5C6D";

    public override string ErrorMessage => "Неверный источник превью: ожидается видео и картинка-кадр";
}

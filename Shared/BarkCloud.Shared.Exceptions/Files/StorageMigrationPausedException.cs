namespace BarkCloud.Shared.Exceptions.Files;

public sealed class StorageMigrationPausedException : BaseGrpcException
{
    public override string ErrorCode => "DAD735C8-1AE8-4F9E-9D95-20C917FA4A41";
    public override string ErrorMessage => "Запись временно приостановлена для переключения S3. Повторите после завершения миграции.";
}

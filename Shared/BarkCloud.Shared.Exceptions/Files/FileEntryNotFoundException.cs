namespace BarkCloud.Shared.Exceptions.Files;

public class FileEntryNotFoundException : BaseGrpcException
{
    public override string ErrorCode => "8E3A9C12-5F1D-4D32-B7F4-1A2B3C4D5E6F";

    public override string ErrorMessage => "Запись о файле в облаке не найдена";
}

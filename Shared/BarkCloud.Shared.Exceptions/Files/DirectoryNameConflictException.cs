namespace BarkCloud.Shared.Exceptions.Files;

public class DirectoryNameConflictException : BaseGrpcException
{
    public override string ErrorCode => "C0A4E97C-2E73-4D5D-AB1A-7E3DC8A1D8F1";

    public override string ErrorMessage => "Папка или файл с таким именем уже существует";
}

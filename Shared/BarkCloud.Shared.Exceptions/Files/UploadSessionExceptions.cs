using System.Globalization;

namespace BarkCloud.Shared.Exceptions.Files;

public class UploadIdempotencyConflictException : BaseGrpcException
{
    public override string ErrorCode => "4D8A6B92-55EB-4F8D-A16B-B144B9690D71";

    public override string ErrorMessage => "Этот idempotency key уже использован для другого файла";
}

public class UploadQuotaExceededException : BaseGrpcException
{
    public UploadQuotaExceededException() { }

    public UploadQuotaExceededException(long limitBytes, long usedBytes, long reservedBytes, long requestedBytes)
    {
        LimitBytes = limitBytes;
        UsedBytes = usedBytes;
        ReservedBytes = reservedBytes;
        RequestedBytes = requestedBytes;
    }

    public long LimitBytes { get; }
    public long UsedBytes { get; }
    public long ReservedBytes { get; }
    public long RequestedBytes { get; }

    public override string ErrorCode => "64E94D14-CA45-4827-876C-701FE21986B2";

    public override string ErrorMessage => "Недостаточно свободного места в хранилище";

    public override IReadOnlyDictionary<string, string> ErrorMetadata => new Dictionary<string, string>
    {
        ["x-quota-limit"] = LimitBytes.ToString(CultureInfo.InvariantCulture),
        ["x-quota-used"] = UsedBytes.ToString(CultureInfo.InvariantCulture),
        ["x-quota-reserved"] = ReservedBytes.ToString(CultureInfo.InvariantCulture),
        ["x-quota-requested"] = RequestedBytes.ToString(CultureInfo.InvariantCulture)
    };
}

public class UploadSessionNotFoundException : BaseGrpcException
{
    public override string ErrorCode => "0175A747-79E1-4AD6-B2A3-E872CFA6C00D";

    public override string ErrorMessage => "Upload-сессия не найдена";
}

public class UploadSessionStateException : BaseGrpcException
{
    public override string ErrorCode => "CB87FD5A-E892-4EF1-AB0E-790D7C9F4C0B";

    public override string ErrorMessage => "Операция недоступна в текущем состоянии upload-сессии";
}

public class UploadTokenInvalidException : BaseGrpcException
{
    public override string ErrorCode => "EBCEDE5E-6BC0-44F3-8966-6E71C136CE6B";

    public override string ErrorMessage => "Недействительный токен upload-сессии";
}

public class UploadPartInvalidException : BaseGrpcException
{
    public override string ErrorCode => "9D663108-AF9A-45AC-88C4-E7AC9FB743BE";

    public override string ErrorMessage => "Неверный номер или диапазон части файла";
}

public class UploadPartsIncompleteException : BaseGrpcException
{
    public override string ErrorCode => "09BF4D7B-7DB9-4284-BDB0-85457B22A589";

    public override string ErrorMessage => "Не все части файла загружены";
}

public class InvalidUploadSessionRequestException : BaseGrpcException
{
    public override string ErrorCode => "804AA2C8-1BBF-49EE-9FD5-A382BF3040F4";

    public override string ErrorMessage => "Параметры upload-сессии недействительны";
}

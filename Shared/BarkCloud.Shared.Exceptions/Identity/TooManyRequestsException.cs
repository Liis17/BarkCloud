using System.Globalization;

namespace BarkCloud.Shared.Exceptions.Identity;

public class TooManyRequestsException : BaseGrpcException
{
    private readonly int? _retryAfterSeconds;

    // Клиентский интерсептор создаёт исключения по коду через Activator — нужен конструктор без параметров.
    public TooManyRequestsException()
    {
    }

    public TooManyRequestsException(TimeSpan retryAfter)
    {
        _retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
    }

    public override string ErrorCode => "8F2B6D41-5A93-4C7E-B0D8-1E4A7C9F3B26";

    public override string ErrorMessage => "Слишком много запросов. Повторите позже";

    public override IReadOnlyDictionary<string, string> ErrorMetadata => _retryAfterSeconds is { } seconds
        ? new Dictionary<string, string> { ["x-retry-after-seconds"] = seconds.ToString(CultureInfo.InvariantCulture) }
        : new Dictionary<string, string>();
}

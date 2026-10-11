namespace BarkCloud.Shared.Exceptions.Identity;

using System.Globalization;

public class OtpCodeNeedException : BaseGrpcException
{
    private readonly string? _otpMethod;
    private readonly int? _retryAfterSeconds;
    private readonly string? _mailStatus;

    public OtpCodeNeedException() { }

    public OtpCodeNeedException(string? otpMethod, int? retryAfterSeconds = null, string? mailStatus = null)
    {
        _otpMethod = otpMethod;
        _retryAfterSeconds = retryAfterSeconds;
        _mailStatus = mailStatus;
    }

    public override string ErrorCode => "C1576884-12D8-4722-A7EE-9F9789AD1265";

    public override string ErrorMessage => "Необходимо 2FA ввести код";

    public override IReadOnlyDictionary<string, string> ErrorMetadata
    {
        get
        {
            var metadata = new Dictionary<string, string>();
            if (_otpMethod is not null)
                metadata["x-otp-method"] = _otpMethod;
            if (_otpMethod == "email")
            {
                if (_retryAfterSeconds is { } retryAfter)
                    metadata["x-otp-retry-after-seconds"] = retryAfter.ToString(CultureInfo.InvariantCulture);
                if (_mailStatus is not null)
                    metadata["x-otp-mail-status"] = _mailStatus;
            }

            return metadata;
        }
    }
}

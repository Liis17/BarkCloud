using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.DisableOtpVerificationServer;

public class DisableOtpVerificationServerCommandHandler : IRequestHandler<DisableOtpVerificationServerCommand, DisableOtpVerificationResponse>
{
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly ILogger<DisableOtpVerificationServerCommandHandler> _logger;

    public DisableOtpVerificationServerCommandHandler(IAuthPropertiesStorage authPropertiesStorage,
        ILogger<DisableOtpVerificationServerCommandHandler> logger)
    {
        _authPropertiesStorage = authPropertiesStorage;
        _logger = logger;
    }

    public async Task<DisableOtpVerificationResponse> Handle(DisableOtpVerificationServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Принудительное отключение 2FA для пользователя {UserId}, тип: {OtpType}",
            request.UserId, request.OtpType);

        if (request.OtpType == OtpTypeId.Authenticator)
        {
            await _authPropertiesStorage.DisableOtp(request.UserId);
        }

        if (request.OtpType == OtpTypeId.Email)
        {
            await _authPropertiesStorage.DisableEmailOtp(request.UserId);
        }

        _logger.LogInformation(
            "2FA успешно отключена для пользователя {UserId}. Тип: {OtpType}",
            request.UserId, request.OtpType);

        return new DisableOtpVerificationResponse();
    }
}

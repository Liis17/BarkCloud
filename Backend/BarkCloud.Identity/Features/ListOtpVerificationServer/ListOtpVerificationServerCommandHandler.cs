using BarkCloud.Identity.Persistence.Services;
using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.ListOtpVerificationServer;

public class ListOtpVerificationServerCommandHandler : IRequestHandler<ListOtpVerificationServerCommand, ListOtpVerificationResponse>
{
    private readonly IAuthPropertiesStorage _authPropertiesStorage;
    private readonly ILogger<ListOtpVerificationServerCommandHandler> _logger;

    public ListOtpVerificationServerCommandHandler(IAuthPropertiesStorage authPropertiesStorage,
        ILogger<ListOtpVerificationServerCommandHandler> logger)
    {
        _authPropertiesStorage = authPropertiesStorage;
        _logger = logger;
    }

    public async Task<ListOtpVerificationResponse> Handle(ListOtpVerificationServerCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Получение статуса 2FA для пользователя {UserId} (server)", request.UserId);

        var otpAuth = await _authPropertiesStorage.GetUserAuthProperties(request.UserId);

        if (otpAuth is null)
        {
            return new ListOtpVerificationResponse
            {
                AuthenticatorEnabled = false,
                EmailEnabled = false
            };
        }

        return new ListOtpVerificationResponse
        {
            EmailEnabled = otpAuth.EmailOtpEnabled,
            AuthenticatorEnabled = otpAuth.OtpEnabled
        };
    }
}

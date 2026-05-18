using BarkCloud.Proto.Identity;

using MediatR;

namespace BarkCloud.Identity.Features.Logout;

public class LogoutCommand : IRequest<LogoutResponse>;

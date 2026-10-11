using System.Security.Authentication;

using BarkCloud.Drive.Contracts.Localization;

using Grpc.Core;

namespace BarkCloud.Drive.Engine;

internal static class ConnectionErrors
{
    public static bool IsTls(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is AuthenticationException)
                return true;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }

    public static string GetDisplayMessage(Exception exception)
    {
        if (IsTls(exception))
            return Loc.T("Eng_TlsConnectionError");

        return exception is RpcException rpc ? rpc.Status.Detail : exception.Message;
    }
}

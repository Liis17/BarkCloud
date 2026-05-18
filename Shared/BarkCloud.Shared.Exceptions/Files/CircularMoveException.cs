namespace BarkCloud.Shared.Exceptions.Files;

public class CircularMoveException : BaseGrpcException
{
    public override string ErrorCode => "F2B82A3C-1D2E-4B0E-9A19-2D90F8B7C001";

    public override string ErrorMessage => "Нельзя переместить папку внутрь самой себя или своего поддерева";
}

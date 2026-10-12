using Grpc.Core;

namespace BarkCloud.Drive.Engine;

internal static class ServerErrors
{
    public const string InvalidRefreshToken = "7E6A31C5-3C4D-412E-87BC-0A387617A5D3";
    public const string SessionNotFound = "011BF29A-2DE6-4A63-BF8D-3F36AE730D9D";
    public const string FileEntryNotFound = "8E3A9C12-5F1D-4D32-B7F4-1A2B3C4D5E6F";
    public const string DirectoryNameConflict = "C0A4E97C-2E73-4D5D-AB1A-7E3DC8A1D8F1";
    public const string DirectoryNotFound = "B86C8B6E-2A12-44E1-9B6B-1AE0F1C0E1AE";
    public const string CircularMove = "F2B82A3C-1D2E-4B0E-9A19-2D90F8B7C001";
    public const string FileAlreadyAttached = "F1A2B3C4-5D6E-47F8-9A0B-1C2D3E4F5A6B";
    public const string FileNotReady = "4E6B7B02-D071-446A-858D-E97D2F154120";
    public const string CloudAccessDenied = "D4F1E2A8-9C5B-4A77-83BB-5E6F7A8B9C01";
    public const string UploadIdempotencyConflict = "4D8A6B92-55EB-4F8D-A16B-B144B9690D71";
    public const string UploadQuotaExceeded = "64E94D14-CA45-4827-876C-701FE21986B2";
    public const string UploadSessionNotFound = "0175A747-79E1-4AD6-B2A3-E872CFA6C00D";
    public const string UploadSessionState = "CB87FD5A-E892-4EF1-AB0E-790D7C9F4C0B";
    public const string UploadTokenInvalid = "EBCEDE5E-6BC0-44F3-8966-6E71C136CE6B";
    public const string UploadPartInvalid = "9D663108-AF9A-45AC-88C4-E7AC9FB743BE";
    public const string UploadPartsIncomplete = "09BF4D7B-7DB9-4284-BDB0-85457B22A589";
    public const string InvalidUploadSessionRequest = "804AA2C8-1BBF-49EE-9FD5-A382BF3040F4";

    public static Guid? GetCode(RpcException exception)
    {
        foreach (var trailer in exception.Trailers)
        {
            if (!trailer.IsBinary
                && string.Equals(trailer.Key, "x-error-code", StringComparison.OrdinalIgnoreCase))
                return Guid.TryParse(trailer.Value, out var code) ? code : null;
        }

        return null;
    }

    public static bool HasCode(RpcException exception, string expectedCode)
        => Guid.TryParse(expectedCode, out var expected) && GetCode(exception) == expected;
}

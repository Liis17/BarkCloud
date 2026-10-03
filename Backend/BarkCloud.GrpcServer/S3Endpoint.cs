using System.Security.Cryptography;
using System.Text.Json;

namespace BarkCloud.GrpcServer;

/// <summary>The endpoint actually used by S3 clients, including the R2 HTTPS override.</summary>
public static class S3Endpoint
{
    public static string Normalize(string value, bool isR2 = false)
    {
        var uri = new Uri(value.Trim());
        if (isR2) uri = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string ConnectionHash(string endpoint, string bucket, string accessKey, string secretKey,
        string region, bool forcePathStyle, bool isR2) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Endpoint = Normalize(endpoint, isR2), Bucket = bucket, AccessKey = accessKey, SecretKey = secretKey,
            Region = region, ForcePathStyle = forcePathStyle, IsR2 = isR2 })));
}

using System.Text.Json;
using System.Text.Json.Nodes;

using DSInternals.Win32.WebAuthn;

using B64 = System.Buffers.Text.Base64Url;

namespace BarkCloud.Drive.App;

// Вход по ключу безопасности через Windows WebAuthn API (webauthn.dll, managed-обёртка
// DSInternals). Парсит серверные options, вызывает системный диалог (PIN + касание),
// собирает assertion в формате, который ждёт сервер (Fido2NetLib).
internal static class WebAuthnClient
{
    // webauthn.dll доступен начиная с Windows 10 1903 (build 18362).
    public static bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 18362);

    public static string GetAssertion(nint hwnd, string rpId, string optionsJson)
    {
        using var doc = JsonDocument.Parse(optionsJson);
        var root = doc.RootElement;

        var challenge = B64.DecodeFromChars(root.GetProperty("challenge").GetString()!);

        var allow = new List<PublicKeyCredentialDescriptor>();
        if (root.TryGetProperty("allowCredentials", out var ac) && ac.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in ac.EnumerateArray())
            {
                if (c.TryGetProperty("id", out var idEl) && idEl.GetString() is { } id)
                    allow.Add(new PublicKeyCredentialDescriptor(B64.DecodeFromChars(id)));
            }
        }

        var api = new WebAuthnApi();
        var cred = api.AuthenticatorGetAssertion(
            rpId: rpId,
            challenge: challenge,
            userVerificationRequirement: ParseUv(root),
            allowCredentials: allow.Count > 0 ? allow : null,
            windowHandle: new WindowHandle(hwnd));

        var response = cred.Response;

        var assertion = new JsonObject
        {
            ["id"] = B64.EncodeToString(cred.Id),
            ["rawId"] = B64.EncodeToString(cred.RawId),
            ["type"] = string.IsNullOrEmpty(cred.Type) ? "public-key" : cred.Type,
            ["response"] = new JsonObject
            {
                ["authenticatorData"] = B64.EncodeToString(response.AuthenticatorData!),
                ["clientDataJSON"] = B64.EncodeToString(System.Text.Encoding.UTF8.GetBytes(response.ClientDataJson!)),
                ["signature"] = B64.EncodeToString(response.Signature!),
                ["userHandle"] = response.UserHandle is { Length: > 0 } uh
                    ? B64.EncodeToString(uh)
                    : null
            }
        };

        return assertion.ToJsonString();
    }

    private static UserVerificationRequirement ParseUv(JsonElement root)
    {
        var uv = root.TryGetProperty("userVerification", out var el) ? el.GetString() : null;
        return uv switch
        {
            "required" => UserVerificationRequirement.Required,
            "discouraged" => UserVerificationRequirement.Discouraged,
            _ => UserVerificationRequirement.Preferred
        };
    }
}

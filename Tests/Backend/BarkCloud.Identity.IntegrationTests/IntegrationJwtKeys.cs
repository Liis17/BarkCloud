using BarkCloud.TestKit;

namespace BarkCloud.Identity.IntegrationTests;

/// <summary>
/// Общая пара RSA-ключей для интеграционных тестов, выпускающих настоящие JWT через JwtService.
/// Статическая: файлы ключей живут весь прогон сборки.
/// </summary>
public static class IntegrationJwtKeys
{
    public static readonly RsaJwtTestKeys Instance = new();
}

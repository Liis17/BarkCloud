namespace BarkCloud.Web;

/// <summary>
/// Единый источник версии веб-приложения. Отсюда версия попадает на страницу
/// (сайдбар и раздел «Обслуживание») и в device-info при входе/регистрации.
/// Значение можно переопределить конфигом <c>App:Version</c>; если он не задан — берётся отсюда.
///
/// ВАЖНО (правило проекта): при любых изменениях кода веб-версии (Backend/BarkCloud.Web,
/// включая ClientApp) поднимай <see cref="Current"/>.
/// </summary>
public static class AppVersion
{
    public const string Current = "v1.1.5";
}

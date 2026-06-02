using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace BarkCloud.Drive.Contracts.Localization;

// Доступ к локализованным строкам (resx в этой сборке, общие для App и Engine).
// Текущий язык хранится явным полем (а не CultureInfo.CurrentUICulture потока) —
// чтобы строки одинаково локализовались на любом потоке: обработчики StreamJsonRpc
// и колбэки Dokany выполняются на разных потоках.
// Индексатор + INotifyPropertyChanged позволяют XAML-привязкам обновляться на лету
// при смене языка (см. TrExtension в App). В Engine используется статический T(...).
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly ResourceManager Rm =
        new("BarkCloud.Drive.Contracts.Localization.Strings", typeof(Loc).Assembly);

    private static CultureInfo _culture = CultureInfo.CurrentUICulture;

    // Текущий код языка (ru/en/de) — например, чтобы передать его движку по IPC.
    public static string CurrentCode => _culture.TwoLetterISOLanguageName;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Для XAML: {Binding [Key], Source=Loc.Instance}.
    public string this[string key] => Rm.GetString(key, _culture) ?? key;

    // Для code-behind / Engine.
    public static string T(string key) => Rm.GetString(key, _culture) ?? key;

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    // Сменить язык: запоминает культуру и обновляет все XAML-привязки.
    public static void SetCulture(string code)
    {
        _culture = new CultureInfo(code);
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
    }
}

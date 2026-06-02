using System.Windows.Data;
using System.Windows.Markup;

using BarkCloud.Drive.Contracts.Localization;

namespace BarkCloud.Drive.App.Localization;

// XAML-расширение для локализации: {loc:Tr Some_Key}.
// Возвращает OneWay-привязку на индексатор Loc.Instance[key]; при смене языка
// Loc поднимает PropertyChanged("Item[]") и все такие привязки перечитываются.
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}

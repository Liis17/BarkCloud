using System.Globalization;

namespace BarkCloud.Web.Rendering;

/// <summary>Форматирование значений в виде, привычном для русскоязычного UI.</summary>
public static class Format
{
    // chiseled-образы без ICU работают в globalization-invariant mode, где доступна
    // только инвариантная культура. Не валимся, а аккуратно деградируем.
    private static readonly CultureInfo Ru = ResolveRu();

    private static CultureInfo ResolveRu()
    {
        try
        {
            return CultureInfo.GetCultureInfo("ru-RU");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private const double Kb = 1024d;
    private const double Mb = Kb * 1024d;
    private const double Gb = Mb * 1024d;
    private const double Tb = Gb * 1024d;

    /// <summary>"312,4 ГБ", "1,1 МБ" и т.п.</summary>
    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 Б";

        return bytes switch
        {
            >= (long)Tb => $"{(bytes / Tb).ToString("0.#", Ru)} ТБ",
            >= (long)Gb => $"{(bytes / Gb).ToString("0.#", Ru)} ГБ",
            >= (long)Mb => $"{(bytes / Mb).ToString("0.#", Ru)} МБ",
            >= (long)Kb => $"{(bytes / Kb).ToString("0.#", Ru)} КБ",
            _ => $"{bytes} Б"
        };
    }

    public static string Date(DateTimeOffset value)
        => value.ToLocalTime().ToString("d MMMM yyyy", Ru);

    public static string Time(DateTimeOffset value)
        => value.ToLocalTime().ToString("HH:mm", Ru);

    /// <summary>"Сегодня", "Вчера", "4 дня назад", "Месяц назад".</summary>
    public static string Relative(DateTimeOffset value)
    {
        var delta = DateTimeOffset.UtcNow - value.ToUniversalTime();

        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1) return "только что";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} мин назад";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours} ч назад";

        var days = (int)delta.TotalDays;
        return days switch
        {
            0 => "сегодня",
            1 => "вчера",
            < 7 => $"{days} дн назад",
            < 31 => $"{days / 7} нед назад",
            < 365 => $"{days / 30} мес назад",
            _ => $"{days / 365} г назад"
        };
    }

    public static int Percent(long used, long total)
        => total <= 0 ? 0 : (int)Math.Clamp(Math.Round(used * 100d / total), 0, 100);

    /// <summary>Размер в гигабайтах без единицы измерения, напр. "312,4".</summary>
    public static string ToGb(long bytes)
        => (bytes / Gb).ToString("0.#", Ru);

    public static string Initials(string firstName, string lastName)
    {
        var f = string.IsNullOrEmpty(firstName) ? "" : firstName[..1];
        var l = string.IsNullOrEmpty(lastName) ? "" : lastName[..1];
        var initials = (f + l).ToUpper(Ru);
        return string.IsNullOrEmpty(initials) ? "?" : initials;
    }
}

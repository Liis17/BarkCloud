namespace BarkCloud.Web;

/// <summary>Метка запуска текущего процесса web для страниц ожидания.</summary>
public static class WebRuntime
{
    public static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.UtcNow;
}

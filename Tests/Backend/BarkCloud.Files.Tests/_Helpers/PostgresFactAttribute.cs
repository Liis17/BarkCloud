namespace BarkCloud.Files.Tests._Helpers;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() => Skip = PostgresFilesDatabase.SkipReason;
}

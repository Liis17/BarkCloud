namespace BarkCloud.Files.Tests._Helpers;

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = PostgresFilesDatabase.SkipReason;
}

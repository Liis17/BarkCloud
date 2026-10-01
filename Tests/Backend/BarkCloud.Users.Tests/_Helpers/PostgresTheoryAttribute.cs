namespace BarkCloud.Users.Tests._Helpers;

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() => Skip = PostgresUsersDatabase.SkipReason;
}

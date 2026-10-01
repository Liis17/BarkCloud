namespace BarkCloud.Users.Tests._Helpers;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute() => Skip = PostgresUsersDatabase.SkipReason;
}

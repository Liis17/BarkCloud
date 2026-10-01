using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Users.Persistence.Migrations
{
    public partial class EnforceUniqueUserLogins : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("LOCK TABLE \"Users\", \"UserContacts\" IN SHARE ROW EXCLUSIVE MODE;");
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    duplicate_usernames text;
                    duplicate_emails text;
                BEGIN
                    SELECT string_agg(user_ids::text, '; ')
                    INTO duplicate_usernames
                    FROM (
                        SELECT array_agg("Id" ORDER BY "Id") AS user_ids
                        FROM "Users"
                        GROUP BY lower("Username")
                        HAVING count(*) > 1
                        LIMIT 20
                    ) duplicates;

                    SELECT string_agg(user_ids::text, '; ')
                    INTO duplicate_emails
                    FROM (
                        SELECT array_agg("UserId" ORDER BY "UserId") AS user_ids
                        FROM "UserContacts"
                        WHERE "Email" <> ''
                        GROUP BY lower("Email")
                        HAVING count(*) > 1
                        LIMIT 20
                    ) duplicates;

                    IF duplicate_usernames IS NOT NULL OR duplicate_emails IS NOT NULL THEN
                        RAISE EXCEPTION 'F04: обнаружены дубли логинов. Username user_ids=[%]; Email user_ids=[%]. Разрешите дубли перед миграцией (показано до 20 групп каждого типа).',
                            coalesce(duplicate_usernames, ''), coalesce(duplicate_emails, '');
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"UX_Users_Username_Lower\" ON \"Users\" (lower(\"Username\"));");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"UX_UserContacts_Email_Lower\" ON \"UserContacts\" (lower(\"Email\")) WHERE \"Email\" <> '';");
            migrationBuilder.Sql("DROP INDEX \"IX_Users_Username_Lower\";");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_Users_Username_Lower\" ON \"Users\" (lower(\"Username\"));");
            migrationBuilder.Sql("DROP INDEX \"UX_UserContacts_Email_Lower\";");
            migrationBuilder.Sql("DROP INDEX \"UX_Users_Username_Lower\";");
        }
    }
}

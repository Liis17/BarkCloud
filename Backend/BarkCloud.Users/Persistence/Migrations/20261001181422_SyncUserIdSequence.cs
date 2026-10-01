using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BarkCloud.Users.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SyncUserIdSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE "Users" IN SHARE ROW EXCLUSIVE MODE;

                DO $sync_user_id$
                DECLARE
                    user_sequence regclass := pg_get_serial_sequence('"Users"', 'Id')::regclass;
                    sequence_last_value bigint;
                    sequence_is_called boolean;
                    current_next_id bigint;
                    next_user_id bigint;
                BEGIN
                    EXECUTE format('SELECT last_value, is_called FROM %s', user_sequence)
                        INTO sequence_last_value, sequence_is_called;
                    current_next_id := sequence_last_value + CASE WHEN sequence_is_called THEN 1 ELSE 0 END;

                    SELECT GREATEST(COALESCE(MAX("Id"), 0) + 1, current_next_id)
                        INTO next_user_id
                        FROM "Users";

                    IF next_user_id > current_next_id THEN
                        EXECUTE format('ALTER SEQUENCE %s RESTART WITH %s', user_sequence, next_user_id);
                    END IF;
                END;
                $sync_user_id$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Возврат sequence назад может повторно выдать уже использованные ID.
        }
    }
}

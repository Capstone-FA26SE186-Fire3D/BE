using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The preceding legacy migration is known to omit password_hash from its snapshot.
            // Keep pre-existing hashes intact when this migration is applied to a database that
            // already has the column.
            migrationBuilder.Sql("ALTER TABLE public.users ADD COLUMN IF NOT EXISTS password_hash text NULL;");
            migrationBuilder.Sql("ALTER TABLE public.users ADD COLUMN IF NOT EXISTS username varchar(30) NULL;");
            migrationBuilder.Sql("ALTER TABLE public.organizations ADD COLUMN IF NOT EXISTS address text NULL;");
            migrationBuilder.Sql("ALTER TABLE public.organizations ADD COLUMN IF NOT EXISTS phone varchar(50) NULL;");

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF EXISTS (
                    SELECT 1 FROM public.users
                    WHERE username IS NOT NULL
                    GROUP BY lower(username)
                    HAVING count(*) > 1
                  ) THEN
                    RAISE EXCEPTION 'Cannot create username uniqueness index: duplicate usernames exist.';
                  END IF;
                END $$;
                """);
            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS users_username_lower_key ON public.users (lower(username)) WHERE username IS NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE public.users ADD CONSTRAINT check_user_username CHECK (username IS NULL OR (username = lower(username) AND username ~ '^[a-z0-9._-]{3,30}$')) NOT VALID;");
            // Existing accounts are retained and can complete a username later. PostgreSQL still
            // validates every newly inserted Trainee against this constraint.
            migrationBuilder.Sql("ALTER TABLE public.users ADD CONSTRAINT check_trainee_username_required CHECK (role <> 'Trainee' OR username IS NOT NULL) NOT VALID;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE public.users DROP CONSTRAINT IF EXISTS check_trainee_username_required;");
            migrationBuilder.Sql("ALTER TABLE public.users DROP CONSTRAINT IF EXISTS check_user_username;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.users_username_lower_key;");
            migrationBuilder.DropColumn(
                name: "username",
                table: "users");

            migrationBuilder.DropColumn(
                name: "address",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "organizations");
        }
    }
}

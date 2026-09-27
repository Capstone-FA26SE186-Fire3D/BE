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
            // A CHECK constraint would also reject an unrelated UPDATE to a legacy Trainee that
            // has no username. The trigger protects new Trainees while allowing those accounts
            // to sign in and complete their profile.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.require_username_for_new_trainee()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW.role = 'Trainee' AND NEW.username IS NULL THEN
                    RAISE EXCEPTION 'Trainee username is required' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END $$;
                DROP TRIGGER IF EXISTS users_require_username_for_new_trainee ON public.users;
                CREATE TRIGGER users_require_username_for_new_trainee
                  BEFORE INSERT OR UPDATE OF role, username ON public.users
                  FOR EACH ROW EXECUTE FUNCTION public.require_username_for_new_trainee();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS users_require_username_for_new_trainee ON public.users;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.require_username_for_new_trainee();");
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

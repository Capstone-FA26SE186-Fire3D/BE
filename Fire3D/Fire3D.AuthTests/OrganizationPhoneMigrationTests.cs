using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class OrganizationPhoneMigrationTests
{
    internal static async Task Apply(BillingDatabase db)
    {
        var type = typeof(AddBuildingBilling).Assembly.GetType("Fire3D.Infrastructure.Migrations.AddOrganizationPhoneUniqueness");
        Assert.NotNull(type);
        var migration = (Migration)Activator.CreateInstance(type)!;
        var sql = string.Join("\n", migration.UpOperations.OfType<SqlOperation>().Select(x => x.Sql));
        await db.Sql("BEGIN;\n" + sql + "\nCOMMIT;");
    }

    internal static string Preflight()
    {
        using var stream = typeof(AddBuildingBilling).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.OrganizationPhonePreflight.sql");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim().TrimEnd(';');
    }

    [BillingPostgresFact]
    public async Task Additive_migration_preserves_legacy_format_revision_and_multiple_nulls()
    {
        await using var db = await BillingDatabase.Create(false);
        await db.Sql($"UPDATE organizations SET phone=' (070) 636-4866 ',profile_revision=7,is_active=false,deleted_at=now() WHERE id='{BillingDatabase.Org}'");
        var before = await db.Scalar("SELECT jsonb_agg(to_jsonb(o) ORDER BY id)::text FROM organizations o");
        await Apply(db);
        Assert.Equal(before, await db.Scalar("SELECT jsonb_agg(to_jsonb(o) ORDER BY id)::text FROM organizations o"));
        await db.Sql("INSERT INTO organizations(id,name,slug,phone,plan,is_active,metadata,created_at,updated_at) VALUES(gen_random_uuid(),'Null phone','null-phone',NULL,'free',true,'{}',now(),now())");
        Assert.Equal(2L, await db.Scalar("SELECT count(*) FROM organizations WHERE phone IS NULL"));
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Sql($"UPDATE organizations SET phone='0706364866' WHERE id='{BillingDatabase.OtherOrg}'"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal("organizations_phone_normalized_key", error.ConstraintName);
    }

    [BillingPostgresFact]
    public async Task Migration_refuses_duplicate_or_invalid_data_without_changing_rows_or_leaving_index()
    {
        foreach (var other in new[] { "(070) 636-4866", "", "abc0706364867", "-+123456", "+12+3456", "12345" })
        {
            await using var db = await BillingDatabase.Create(false);
            await using (var connection = new NpgsqlConnection(db.Connection))
            {
                await connection.OpenAsync();
                using var seed = new NpgsqlCommand($"UPDATE organizations SET phone='0706364866' WHERE id='{BillingDatabase.Org}'; UPDATE organizations SET phone=@phone WHERE id='{BillingDatabase.OtherOrg}'", connection);
                seed.Parameters.AddWithValue("phone", other); await seed.ExecuteNonQueryAsync();
            }
            var before = await db.Scalar("SELECT jsonb_agg(to_jsonb(o) ORDER BY id)::text FROM organizations o");
            var error = await Assert.ThrowsAsync<PostgresException>(() => Apply(db));
            Assert.Contains("Organization phone preflight failed", error.MessageText);
            if (other.Length > 0) Assert.DoesNotContain(other, error.MessageText);
            Assert.Equal(before, await db.Scalar("SELECT jsonb_agg(to_jsonb(o) ORDER BY id)::text FROM organizations o"));
            Assert.Equal(true, await db.Scalar("SELECT to_regclass('public.organizations_phone_normalized_key') IS NULL"));
        }
    }

    [BillingPostgresFact]
    public async Task Read_only_preflight_matches_application_validation_and_only_returns_masked_issues()
    {
        await using var db = await BillingDatabase.Create(false);
        var inputs = new List<string> { "0706364866", " (070) 636-4866 ", "+84 (706) 364-866", "000000", "123456789012345", "", "12345", "1234567890123456", "abc123456", "１２３４５６", "-+123456", "123456+", "++123456", "\t123\t456", new string(' ', 44) + "123456", "v123456" };
        // .NET Trim recognizes these boundary whitespace characters; SQL must agree independently of DB locale.
        foreach (var ch in Enumerable.Range(1, 65535).Select(i => (char)i).Where(char.IsWhiteSpace))
            inputs.Add(ch + "(070) 636-4866" + ch);
        await using var connection = new NpgsqlConnection(db.Connection); await connection.OpenAsync();
        foreach (var input in inputs)
        {
            using var update = new NpgsqlCommand($"UPDATE organizations SET phone=@phone WHERE id='{BillingDatabase.Org}'", connection);
            update.Parameters.AddWithValue("phone", input); await update.ExecuteNonQueryAsync();
            var result = GoogleAuthRules.Validate(new("proof", UserRole.OrganizationUser, OrganizationName: "Org", OrganizationAddress: "Address", OrganizationPhoneNumber: input), new DateOnly(2026, 10, 6));
            await using var tx = await connection.BeginTransactionAsync();
            await new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx).ExecuteNonQueryAsync();
            using var preflight = new NpgsqlCommand(Preflight(), connection, tx);
            await using (var rows = await preflight.ExecuteReaderAsync())
            {
                Assert.Equal(!result.IsSuccess, await rows.ReadAsync());
                if (!result.IsSuccess)
                {
                    Assert.Equal("INVALID_PHONE", rows.GetString(rows.GetOrdinal("issue")));
                    Assert.StartsWith("***", rows.GetString(rows.GetOrdinal("masked_phone")));
                    Assert.DoesNotContain("phone", Enumerable.Range(0, rows.FieldCount).Select(rows.GetName));
                    Assert.False(await rows.ReadAsync());
                }
            }
            await tx.RollbackAsync();
            if (result.IsSuccess)
            {
                using var canonical = new NpgsqlCommand("SELECT regexp_replace(@phone,'[^0-9+]','','g')", connection);
                canonical.Parameters.AddWithValue("phone", input);
                Assert.Equal(result.Value!.OrganizationPhoneNumber, await canonical.ExecuteScalarAsync());
            }
        }
        await db.Sql($"UPDATE organizations SET phone=' (070) 636-4866 ' WHERE id='{BillingDatabase.Org}'; UPDATE organizations SET phone='0706364866',is_active=false,deleted_at=now() WHERE id='{BillingDatabase.OtherOrg}'");
        using var duplicates = new NpgsqlCommand(Preflight(), connection);
        await using var duplicateRows = await duplicates.ExecuteReaderAsync();
        var count = 0;
        while (await duplicateRows.ReadAsync())
        { count++; Assert.Equal("DUPLICATE_PHONE", duplicateRows.GetString(duplicateRows.GetOrdinal("issue"))); Assert.Equal("***866", duplicateRows.GetString(duplicateRows.GetOrdinal("masked_phone"))); }
        Assert.Equal(2, count);
    }

    [BillingPostgresFact]
    public async Task Direct_concurrent_runtime_writes_cannot_commit_the_same_canonical_phone()
    {
        await using var db = await BillingDatabase.Create(false); await GoogleOnboardingPostgresTests.Prepare(db); await Apply(db);
        var runtime = new NpgsqlConnectionStringBuilder(db.Connection) { Options = "-c role=fire3d_api" }.ConnectionString;
        await using var first = new NpgsqlConnection(runtime); await first.OpenAsync();
        await using var second = new NpgsqlConnection(runtime); await second.OpenAsync();
        await using var tx = await first.BeginTransactionAsync();
        const string insert = "INSERT INTO organizations(id,name,slug,phone,plan,is_active,metadata,created_at,updated_at) VALUES(gen_random_uuid(),'Concurrent',@slug,@phone,'free',true,'{}',now(),now())";
        using var winner = new NpgsqlCommand(insert, first, tx); winner.Parameters.AddWithValue("slug", "winner"); winner.Parameters.AddWithValue("phone", "0706364866"); await winner.ExecuteNonQueryAsync();
        using var loser = new NpgsqlCommand(insert, second); loser.Parameters.AddWithValue("slug", "loser"); loser.Parameters.AddWithValue("phone", "(070) 636-4866");
        var waiting = loser.ExecuteNonQueryAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Equals(await db.Scalar("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='transactionid')"), true))
        { Assert.False(waiting.IsCompleted, "Second connection must wait for the uncommitted phone."); await Task.Delay(20, timeout.Token); }
        await tx.CommitAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await waiting);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState); Assert.Equal("organizations_phone_normalized_key", error.ConstraintName);
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM organizations"));
    }
}

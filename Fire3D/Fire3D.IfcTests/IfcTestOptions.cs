using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql.NameTranslation;

namespace Fire3D.IfcTests;

internal static class IfcTestOptions
{
    public static DbContextOptions<Fire3DDbContext> Create(string connection) =>
        new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(connection, pg =>
        {
            var names = new NpgsqlNullNameTranslator();
            pg.MapEnum<UserRole>("user_role_enum", nameTranslator: names);
            pg.MapEnum<FileType>("file_type_enum", nameTranslator: names);
            pg.MapEnum<RevisionStatus>("revision_status_enum", nameTranslator: names);
            pg.MapEnum<QuarantineStatus>("quarantine_status_enum", nameTranslator: names);
            pg.MapEnum<AuditAction>("audit_action_enum", nameTranslator: names);
        }).Options;
}

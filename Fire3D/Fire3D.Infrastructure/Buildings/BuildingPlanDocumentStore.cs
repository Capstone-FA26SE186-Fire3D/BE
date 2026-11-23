using Fire3D.Application.Buildings;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Buildings;

/// <summary>building_plan_documents (BE#212), read and written by the API role; a stored document is immutable (trigger).</summary>
public sealed class BuildingPlanDocumentStore(Fire3DDbContext db) : IBuildingPlanDocumentStore
{
    private const string Columns = """
        id AS "Id", building_id AS "BuildingId", created_by AS "CreatedBy", input_hash AS "InputHash", file_name AS "FileName", content_type AS "ContentType",
        size_bytes AS "SizeBytes", staging_key AS "StagingKey", object_key AS "ObjectKey", sha256 AS "Sha256",
        created_at AS "CreatedAt", expires_at AS "ExpiresAt", completed_at AS "CompletedAt"
        """;
    // Constant SQL; values are always bound as parameters ({0}, {1}).
    private const string SelectByKey = "SELECT " + Columns + " FROM public.building_plan_documents WHERE created_by={0} AND idempotency_key={1}";
    private const string SelectById = "SELECT " + Columns + " FROM public.building_plan_documents WHERE id={0}";
    private const string SelectCurrent = "SELECT " + Columns + " FROM public.building_plan_documents WHERE id=(SELECT signed_plan_document_id FROM public.buildings WHERE id={0})";

    public Task<Guid?> BuildingOrganizationAsync(Guid buildingId, CancellationToken ct) =>
        db.Buildings.AsNoTracking().Where(x => x.Id == buildingId && x.IsActive && x.DeletedAt == null).Select(x => (Guid?)x.OrganizationId).SingleOrDefaultAsync(ct);

    public async Task<BuildingPlanDocumentRecord> CreateOrGetIntentAsync(BuildingPlanDocumentRecord c, string idempotencyKey, CancellationToken ct)
    {
        // The first request under (actor, key) inserts; any later one, even concurrent, reads that row back.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.building_plan_documents(id,building_id,created_by,idempotency_key,input_hash,file_name,content_type,size_bytes,staging_key,created_at,expires_at)
            VALUES({c.Id},{c.BuildingId},{c.CreatedBy},{idempotencyKey},{c.InputHash},{c.FileName},{c.ContentType},{c.SizeBytes},{c.StagingKey},{c.CreatedAt},{c.ExpiresAt})
            ON CONFLICT (created_by,idempotency_key) DO NOTHING
            """, ct);
        return await db.Database.SqlQueryRaw<BuildingPlanDocumentRecord>(SelectByKey, c.CreatedBy, idempotencyKey).SingleAsync(ct);
    }

    public Task<BuildingPlanDocumentRecord?> FindAsync(Guid id, CancellationToken ct) =>
        db.Database.SqlQueryRaw<BuildingPlanDocumentRecord>(SelectById, id).SingleOrDefaultAsync(ct);

    public Task<BuildingPlanDocumentRecord?> CurrentAsync(Guid buildingId, CancellationToken ct) =>
        db.Database.SqlQueryRaw<BuildingPlanDocumentRecord>(SelectCurrent, buildingId).SingleOrDefaultAsync(ct);

    public async Task<bool> CompleteAsync(Guid id, string objectKey, string sha256, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.building_plan_documents SET object_key={objectKey},sha256={sha256},completed_at=clock_timestamp()
            WHERE id={id} AND completed_at IS NULL
            """, ct) == 1;

    public async Task AttachAsync(Guid buildingId, Guid documentId, DateTime now, CancellationToken ct)
    {
        var building = await db.Buildings.SingleAsync(x => x.Id == buildingId, ct);
        building.SignedPlanDocumentId = documentId;
        building.UpdatedAt = BuildingEtag.Next(now, building.UpdatedAt);
        await db.SaveChangesAsync(ct);
    }
}

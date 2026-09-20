namespace Fire3D.Application.Ifc;
public sealed record ProcessingJobResponse(Guid Id, Guid RevisionId, Guid SourceDocumentId, Guid? ScenarioVersionId,
    string Kind, string Status, DateTime CreatedAt);

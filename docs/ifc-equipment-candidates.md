# Equipment detected from IFC (IFC-04)

The IFC worker proposes interactive equipment (fire equipment, water sources, doors, elevators) from the IFC; an OrganizationUser
confirms, retypes or rejects each candidate. Rule from Docs [IFC ingestion](../../Docs/ifc_ingestion_visual_interaction.md) 5.2/5.7:
when the IFC has fire equipment or water sources the pipeline suggests them; when it has none the OrganizationUser places them in the
editor. The 3D model always comes from the shared asset library, never from the IFC. Migration `20261015091000_AddEquipmentCandidates`.

## Worker contract: `building_metadata`

A Geometry attempt sends a second output beside `preview_glb`: `artifactType: "building_metadata"`, metadata with
`schemaVersion: "fet3d.building/1"` ([schema](../contracts/ifc/v1/building-metadata.schema.json),
[fixture](../contracts/ifc/v1/fixtures/building-metadata.valid.json)).

- `interactables[]` are the candidates and are validated field by field before the gate accepts the output (`422 BUILDING_METADATA_INVALID`
  with `issues`; another schemaVersion `422 BUILDING_METADATA_VERSION_UNSUPPORTED`): `id` (unique, e.g. `ifc:<GlobalId>`), `ifcGlobalId`,
  `catalogKey` (capability id), `source` (`ifc` | `inferred`), `confidence` 0–1, `position` [x, y, z] metres in the floor frame, optional
  `ifcClass`, `floorId`, `rotationY` (degrees), `bbox` [w, d, h] metres. At most 5000.
- `sources`, `storeys`, `spaces` (with `longName`, `category`), `portals`, `materials`, `qaIssues` only need their container shape for now.
- Put each candidate's `ifcGlobalId` in `preview_glb` `semanticMapping` too, otherwise the editor cannot anchor an Equipment object to it.
- The validation run's candidate artifact stays the `preview_glb` (the gate no longer picks whichever output sorts first).

## Exit door confirmation (`IFC_EXIT_UNKNOWN`, option B)

When the accepted Geometry run carries the warning `IFC_EXIT_UNKNOWN`, the revision is `NeedsFix` and the view returns
`exitConfirmationRequired: true` with `revisionStatus`. Saving a decision that Confirms or Retypes an `IfcDoor` candidate (worker must
send `ifcClass` IfcDoor and its `ifcGlobalId`) as `door.exit` moves the revision to `ReadyForScenario`; when every such door is later
Rejected it returns to `NeedsFix`. The decision is audited (with the resulting revision status) and is suggested again on the next
revision like any other. See [ifc-qa-codes.md](ifc-qa-codes.md).

## API (OrganizationUser of the tenant, PlatformAdmin)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/revisions/{revisionId}/equipment-candidates` | Candidates of the accepted Geometry attempt (current succeeded attempt, Passed QA, no Error/Critical issue) with their latest decision; `ETag` = overlay version (`"0"` before the first save). `needsConfirmation` below confidence 0.9. A candidate without a decision may carry `suggestedDecision` from the newest earlier revision of the Building with the same `ifcGlobalId` — never applied until saved. No accepted geometry → `409 EQUIPMENT_CANDIDATES_UNAVAILABLE`; no `building_metadata` → `metadataAvailable: false`, no items. Other tenant → 404. |
| PATCH | `/api/revisions/{revisionId}/equipment-decisions` | `If-Match` required (428 missing, 400 malformed, 412 stale). Body `{items:[{candidateId, decision: Confirmed\|Retyped\|Rejected, catalogKey?, note?}]}`, 1–500 items, merged over the latest decisions. Confirmed keeps the detected `catalogKey` unless one is sent; Retyped needs `catalogKey`; Rejected takes none. A `catalogKey` must be a capability of an active runtime whose contract allows `Equipment` (`GET /api/scenario-interactions/catalog`). Malformed body (bad decision, duplicate candidateId, catalogKey with Rejected) → `422 CONTENT_INVALID` with `issues`/`errors`; unknown candidate or unsupported catalogKey → `422 EQUIPMENT_DECISION_INVALID` with `errors`. Returns the new view and ETag; audited. |

Decisions are an append-only overlay (`revision_equipment_decisions`, one row per version, immutable): revision artifacts never change.
The editor turns a confirmed candidate into an `Equipment` object of `fet3d.editor/1` (`anchorId` = `ifcGlobalId`, `capability` =
`catalogKey`, parameters from the capability schema); the backend does not edit drafts.

Tests: `AuthIntegrationTests.Equipment_candidates_are_confirmed_with_etags_and_suggested_on_the_next_revision`,
`BuildingMetadataValidatorTests`.

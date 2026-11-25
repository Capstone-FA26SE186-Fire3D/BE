# IFC QA codes (IFC-03)

Source: Docs `ifc_ingestion_visual_interaction.md` 5.9. The backend stores and returns codes only; FE owns the Vietnamese text and guidance.

Where codes appear:

- `GET /api/revisions/{revisionId}/issues` → `items[].issueCode`, `severity`, `message`, `ifcGlobalIds` (the IFC elements the worker reported, `[]` when none; also in `evidence.ifcGlobalIds`). FE highlights these GlobalIds in the preview.
- Upload (#214): these are HTTP errors, not issues. Limits the web checks first: `GET /api/ifc/upload-limits` (`maxBytes`, default
  `IfcUpload:MaxBytes` 524288000 = 500 MiB; `acceptedExtensions`; `acceptedSchemas`).
  - Initiate: 400 `IFC_UNSUPPORTED_FORMAT` when the filename is not `.ifc` (`errors.originalFilename`); 400 `VALIDATION_ERROR`
    (`errors.fileSizeBytes`) when `fileSizeBytes` is over `maxBytes`. Nothing is created.
  - upload-complete, after size and SHA-256 match the intent, in this order: 422 `IFC_UNSUPPORTED_FORMAT` when the bytes do not start
    with the STEP header `ISO-10303-21;` (renamed `.rvt`, zip, ...); 422 `IFC_UNSUPPORTED_SCHEMA` when the first `FILE_SCHEMA` of the
    HEADER section is not IFC2X3, IFC4 or IFC4X3 (suffixed variants such as `IFC2X3_TC1`, `IFC4_ADD2_TC1`, `IFC4X3_ADD2` pass;
    `IFC4X1`, `AP214` or a missing `FILE_SCHEMA` fail; `errors.fileSchema` = what the file declares, `errors.supportedSchemas`).
    Because the SHA-256 already matched, the verdict is final for that intent: the Draft revision and upload intent from initiate are
    deleted, the staging object is queued for the cleanup worker at once, and an audit row (`action` Reject, `target_entity`
    revisions, `new_values.operation` IfcUploadRejected, `code`, `quarantineStatus` Rejected, `sha256`, `details`) is written. A
    replay of the same upload-complete then answers 404 `IFC_UPLOAD_INTENT_NOT_FOUND`. Size or hash mismatches (`IFC_SOURCE_MISMATCH`)
    keep the intent, because the client may still PUT the right bytes.
  - #219 precheck, after `FILE_SCHEMA`: the STEP text is scanned while it is hashed (streamed, never held in memory) and counts entity
    instances `#id=NAME(` outside strings and comments: `IFCBUILDINGSTOREY`, `IFCSPACE`, `IFCDOOR`/`IFCDOORSTANDARDCASE`, and every
    `IFCWALL*`/`IFCSLAB*` element (`IFCWALLSTANDARDCASE`, `IFCSLABELEMENTEDCASE`, ...; the `...TYPE` type objects do not count). A missing
    group answers 422 `IFC_PRECHECK_FAILED` with `errors.missing` (`IfcBuildingStorey`, `IfcSpace`, `IfcDoor`, `IfcWall/IfcSlab`) and
    `errors.qaCodes` (`IFC_NO_STOREYS`, `IFC_NO_SPACES`, `IFC_NO_DOORS`, `IFC_NO_WALLS_OR_SLABS`), and is cleaned up like the two errors
    above; no processing job can exist for it.
  - #219 initiate: when the latest Geometry run of the same SHA-256 in the same Organization failed on an `Error`/`Critical` issue,
    initiate answers 409 `IFC_SOURCE_ALREADY_FAILED` with `errors.qaCodes` (that run's blocking codes), `errors.jobId` and The refusal holds only while the same Geometry worker version (toolchain) would judge the file again: after a worker release the same bytes may be uploaded and processed again.
    `errors.validationRunId`, creates no revision and writes an audit row (`target_entity` buildings, `code`, `sha256`, `qaCodes`). A run
    that failed with warnings only, a later passed run, or another Organization does not block the file.
  - An adopted source gets `quarantineStatus` `Accepted` (shown in the Building revision list). Sources uploaded before #214 keep
    `Pending`.

Revision status after Geometry QA (worker `preview_glb` output):

- `Passed` → `ReadyForScenario`.
- `Failed` (the worker reports a blocking issue as `Error`/`Critical`) → `NeedsFix`. The user fixes the IFC and uploads a new revision. `ConfirmedForTraining`, `Superseded` and `Rejected` revisions are not changed. Migration `20261016080000` also moves revisions whose Geometry run had already failed to `NeedsFix`.

| Code | Docs blocking level | Meaning | User guidance (FE text) |
|---|---|---|---|
| `IFC_NO_BODY_GEOMETRY` | Block (walls/slabs/doors/stairs) / Warning (furniture) | Element has no Body representation | Re-export with 3D Body |
| `IFC_BODY_NONSTANDARD_CONTEXT` | Info | Body is in a context other than `Model` | None |
| `IFC_NO_SPACES` | Block | No `IfcSpace` | Enable rooms/zones export |
| `IFC_SPACE_UNNAMED` | Warning | Room has no `Name`/`LongName` | Name the room or classify it in the editor |
| `IFC_EXIT_UNKNOWN` | Warning; revision stays `NeedsFix` until an exit door is confirmed | No external exit door found | Choose an existing door as exit in the equipment screen; no re-upload |
| `IFC_NO_FIRE_EQUIPMENT` | Warning | No fire alarm/extinguishing device | Place it in the editor |
| `IFC_NO_WATER_SOURCE` | Warning | No water source | Place it in the editor if the scenario needs one |
| `IFC_LOW_CONFIDENCE_DEVICE` | Warning | Candidate below the confidence threshold | Confirm or reject (equipment-decisions) |
| `IFC_UNMAPPED_MATERIAL` | Info | Material name not in the dictionary | PlatformAdmin extends the dictionary |
| `IFC_EXCESSIVE_VOIDS` | Warning | Door opening could not be cut | Check that door/wall |
| `IFC_NOT_ARCHITECTURAL` | Block | No `IfcDoor` and no `IfcSpace` (structure/MEP only) | Upload the architectural file |
| `IFC_NO_FLOOR_UNDER_SPACE` | Warning | No slab under a room; a floor was generated from the `IfcSpace` bottom | Confirm or re-export with slabs |
| `IFC_TRIANGLE_BUDGET_EXCEEDED` | Warning | Chunk over the triangle budget after replacement | Re-export at a lower level of detail |
| `IFC_DEFAULT_MASS_PRESENT` | Info | Planning mass present (removed) | None |
| `IFC_UNSUPPORTED_FORMAT` | Block | Upload is not IFC | Export IFC (IFC2X3/IFC4/IFC4X3) |
| `IFC_NO_STOREYS` | Block | No `IfcBuildingStorey`: the model cannot be split into floors | Export with levels/storeys (Revit: levels; ArchiCAD: stories) |
| `IFC_NO_STAIRS_MULTISTOREY` | Block | More than one storey holds a room (`IfcSpace`) but there is no `IfcStair`, `IfcStairFlight`, `IfcRamp` or `IfcRampFlight`, so no route leads off the upper floors | Model or export the stairs or ramps, then upload again |
| `IFC_NO_DOORS` | Block (upload precheck only) | No `IfcDoor`/`IfcDoorStandardCase` | Export doors with the architectural model |
| `IFC_NO_WALLS_OR_SLABS` | Block (upload precheck only) | No `IfcWall*` or `IfcSlab*` element | Export the architectural model, not a single discipline |
| `IFC_UNIT_NOT_METRE` | Warning | The IFC length unit is not metre (millimetre, foot, ...); the worker scales geometry to metres with the IFC unit | Check sizes in the preview; re-export in metres if they look wrong |

Other codes may appear; FE shows them generically with the worker `message`.

`IFC_NO_STOREYS` and `IFC_NO_SPACES` are also returned by the BE upload precheck (#219) in `errors.qaCodes` of 422
`IFC_PRECHECK_FAILED`; `IFC_NO_DOORS` and `IFC_NO_WALLS_OR_SLABS` exist only there.

Who produces the codes: the Geometry worker (Unity repo, `Tools/GeometryWorker`) reads the IFC and reports every code in this table as
an issue of its `preview_glb`/`building_metadata` output. BE checks only the issue shape (`severity`, non-empty `code` and `message`),
stores the issues and derives the run outcome and revision status from `severity`/`outcome`, so it accepts a new code without a
migration. `IFC_NO_STOREYS`, `IFC_NO_STAIRS_MULTISTOREY` and `IFC_UNIT_NOT_METRE` (added 23/11/2026, #214) therefore need the worker
to emit them: `IFC_NO_STOREYS` and `IFC_NO_STAIRS_MULTISTOREY` as `Error` with outcome `Failed` (the revision becomes `NeedsFix`),
`IFC_UNIT_NOT_METRE` as `Warning`.

`IFC_NO_STAIRS_MULTISTOREY` (rule agreed by FE and Unity, 25/11/2026, #232; Unity#161/Unity PR #163, FE#262):

- Multi-storey means more than one storey holding at least one `IfcSpace`. A one-floor Revit export often has a "Roof" storey with the
  roof slab or a "Foundation" storey with footings; counting storeys with any element blocked those files by mistake. People stand only
  where there are rooms, so counting rooms still blocks the case that matters: an upper floor with rooms and no way down. The count comes
  from the entity relationships, so the worker stops before it builds any mesh.
- A ramp connects storeys like a stair: `IfcStair`, `IfcStairFlight`, `IfcRamp` and `IfcRampFlight` all count (the worker's
  `classify.STAIR_CLASSES` and the browser check use the same list).
- The worker emits it as `Error` in the same early-stop group as `IFC_NO_STOREYS` and `IFC_NO_SPACES`; its message names the number of
  storeys with rooms. The web blocks the upload button only when the file states which storey each room belongs to (`IfcRelAggregates`)
  and two or more storeys have rooms; when it cannot tell, it only warns and leaves the decision to the worker.
- BE has no code change for this rule: it stores the worker's `severity`, `code` and `message`, and the BE upload precheck does not check
  stairs.

Mapping to API severity: the worker sends Block as `Error` (or `Critical`) with outcome `Failed`, Warning as `Warning`, Info as `Info`. Error/Critical issues also block confirm-for-training and publish.

`IFC_EXIT_UNKNOWN` (decided 16/10/2026, option B, BE#100; migration `20261016100000`): the worker reports it as `Warning`, so the
Geometry run passes and the geometry is accepted, but the revision stays `NeedsFix`. `GET /api/revisions/{id}/equipment-candidates`
returns `exitConfirmationRequired: true`; FE shows a "choose exit door" step. Confirming or retyping an existing `IfcDoor` candidate
(`ifcClass` IfcDoor…, with `ifcGlobalId`) as `door.exit` through `PATCH .../equipment-decisions` moves the revision to
`ReadyForScenario` without a new upload; rejecting every such door moves it back to `NeedsFix`. `door.exit` must be an `Equipment`
capability of an active runtime. `exit.final`/`exit.stair` stay editor `Goal` objects. If the worker sends the code as `Error`, the
run fails and the user must fix the IFC.

## Room names (bim-facts)

When Geometry QA passes, `building_metadata.spaces` (`fet3d.building/1`) is stored in `bim_facts`: one row per space for `Name`, `LongName` and `Category` (entity `IfcSpace`, `sourceHash` = IFC SHA-256). A space without name and long name gets `qualityFlags: ["SPACE_UNNAMED"]` on its `Name` row. Read with `GET /api/revisions/{revisionId}/bim-facts`.

Reading them (BE#120): facts are ordered by `ifcGlobalId` then `propertyPath`, so the three facts of a room are adjacent and never
split by paging. Optional filters `entityType` (e.g. `IfcSpace`), `ifcGlobalId` (22 characters) and `qualityFlag` (e.g.
`SPACE_UNNAMED`, which returns every fact of a flagged element); invalid values answer `400 VALIDATION_ERROR` per field.
`GET /api/revisions/{revisionId}/bim-facts/elements` returns one item per element, paged by element: `ifcGlobalId`, `entityType`,
`name`, `longName`, `category`, `qualityFlags` (union of its facts), `sourceHash`, `updatedAt`; with `qualityFlag=SPACE_UNNAMED` it
lists the unnamed rooms without loading every fact.

# IFC QA codes (IFC-03)

Source: Docs `ifc_ingestion_visual_interaction.md` 5.9. The backend stores and returns codes only; FE owns the Vietnamese text and guidance.

Where codes appear:

- `GET /api/revisions/{revisionId}/issues` → `items[].issueCode`, `severity`, `message`, `ifcGlobalIds` (the IFC elements the worker reported, `[]` when none; also in `evidence.ifcGlobalIds`). FE highlights these GlobalIds in the preview.
- Upload: `IFC_UNSUPPORTED_FORMAT` is an HTTP error, not an issue — 400 at initiate when the filename is not `.ifc` (`errors.originalFilename`), 422 at upload-complete when the bytes do not start with the STEP header `ISO-10303-21;` (renamed `.rvt`, zip, ...). At initiate nothing is created; at upload-complete the Draft revision and upload intent from initiate remain but the file is never adopted as the revision source.

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

Other codes may appear; FE shows them generically with the worker `message`.

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

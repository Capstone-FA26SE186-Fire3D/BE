# Material style per revision (STYLE-01)

Docs [IFC ingestion](../../Docs/ifc_ingestion_visual_interaction.md) 5.5: IFC carries almost no usable colour or texture, so materials
come from a curated PBR library. PlatformAdmin keeps **style presets** per building type and **colour schemes**; an OrganizationUser
picks a preset and scheme for a revision and can change materials by group. The choice is stored per revision and applied
automatically by the Blender/Unity workers, never by hand. Migration `20261017090000_AddRevisionStyle`.

## Library kinds (PlatformAdmin)

Presets and schemes are Organization Library items, so they use the Library's versions, publish, audit and cache invalidation
(`/api/admin/library/items`, `.../versions`, `/api/admin/library/versions/{id}/publish`). OrganizationUsers read published versions
with `GET /api/library/items?kind=StylePreset` or `kind=ColorScheme`. Sample payloads:
[contracts/style/v1/fixtures/style-presets.sample.json](../contracts/style/v1/fixtures/style-presets.sample.json).

| Kind | Payload |
|---|---|
| `StylePreset` | `buildingTypes` (1–10, e.g. `school`, `hospital`, `office`, `residential`), `keywords?` (room-name tokens the worker matches as whole words to suggest a preset), `materials` (`default` with `floor`, `wall`, `ceiling`, plus up to 50 room categories mapping some surfaces), `defaultScheme?` (ColorScheme code), `description?` |
| `ColorScheme` | `colors` `{wall, accentWall, doorWood, skirting}` as `#RRGGBB`, `description?` |

Room categories are lowercase identifiers matching `building_metadata.spaces[].category` (e.g. `wc`, `kitchen`, `corridor`, `office`,
`lab`, `workshop`, `lobby`). Material keys are library keys such as `floor.tile`, `floor.wood`, `floor.carpet`, `floor.vinyl`,
`floor.epoxy`, `ceiling.tile`, `wall.plaster`, `wall.brick`, `wall.tile`, `concrete`, `wood`, `stone`, `metal.painted`,
`metal.brushed`, `glass.clear`, `glass.frosted`; the worker owns their textures and 5–10 variants each.

## Revision selection (OrganizationUser, PlatformAdmin)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/revisions/{revisionId}/style` | `version` (ETag, 0 before the first save), `selection` (preset and scheme as Library refs with payload, `overrides`, who and when) and `previousSelection` (newest earlier revision of the same Building, a suggestion only). Other tenant 404. |
| PUT | `/api/revisions/{revisionId}/style` | `If-Match` required (428 missing, 400 malformed, 412 stale). Body `{presetVersionId, schemeVersionId?, overrides?:[{room, surface: floor\|wall\|ceiling, material}]}`, at most 200 overrides, one per room and surface, room `default` = every room. Malformed 422 `CONTENT_INVALID`; preset/scheme not a published version of an active item of that kind 422 `STYLE_SELECTION_INVALID`; Superseded/Rejected revision 409 `REVISION_NOT_EDITABLE`. Audited (`RevisionStyle`). |

Every save is a new immutable version (`revision_style_selections`); earlier versions never change, so a package built from one
keeps it ("changing the preset makes a new build, never edits a pinned package"). Without a scheme the worker uses the preset's
`defaultScheme`; with neither, it picks a scheme deterministically per Building. IFC surface colours, when they are real, tint the
textures (Docs 5.5).

## Not yet wired

Workers do not read the selection yet: the Blender/Unity build worker contract (how a build pins the selection version and
records it in provenance) is decided with the worker team. Until then the selection is stored and served for the editor only.

# Hạ tầng PCCC theo revision (BE#213)

Flow 1 PC06, design §3 (`docs/flow1-pc06-target-design.md`); FR-PLAN-04/05/06. Migration `20261124110000_FireInfrastructure`.

Equipment, exits, assembly points and water sources belong to the IFC revision, not to each scenario. They live in a versioned
**fire infrastructure** that a FireSafetyExpert reviews and locks; every scenario of that revision builds on a locked version.

## Lifecycle

`Draft → Submitted → Approved | Rejected`

- Only a **Draft** is edited (`PUT`). `Submitted` freezes the content; `Approved` is the lock. Nothing is deleted, and a decided version never
  changes: the database refuses it.
- A fix after a decision (Approved or Rejected) is a **new version**, copied from the newest one, then submitted and reviewed again.
  Scenarios on the old version keep it.
- At most one open version (Draft or Submitted) per revision.
- `revision` is the ETag. It grows on every change (save, submit, decision).

## API (OrganizationUser of the Building, or PlatformAdmin)

| Method | Route | Notes |
|---|---|---|
| GET | `/api/revisions/{revisionId}/fire-infrastructures` | Every version, newest first. |
| POST | `/api/revisions/{revisionId}/fire-infrastructures` | New Draft. `Idempotency-Key`. 409 `FIRE_INFRASTRUCTURE_OPEN_VERSION_EXISTS` (`errors.id`) while a Draft/Submitted version exists. |
| GET | `/api/fire-infrastructures/{id}` | One version, `ETag`. |
| PUT | `/api/fire-infrastructures/{id}` | `{geometry, runtimeVersion, items}` replaces the Draft. `If-Match`. |
| POST | `/api/fire-infrastructures/{id}/validate` | Dry run of the submit checks: `{isValid, issues[]}`. |
| POST | `/api/fire-infrastructures/{id}/submit` | To the expert queue. `If-Match` + `Idempotency-Key`. |

Response: `id, revisionId, buildingId, buildingName, organizationId, version, status, revision, geometry, runtimeVersion, items,
contentHash, createdBy, createdAt, updatedAt, submittedBy(+Name), submittedAt, decidedBy(+Name), decidedAt, decisionReason, scenarioDraftCount`.

Errors:
- 409 `FIRE_INFRASTRUCTURE_LOCKED` when you PUT or submit an Approved version; 409 `FIRE_INFRASTRUCTURE_NOT_DRAFT` for a Submitted or Rejected one.
- 412 / 428 for a stale or missing `If-Match`.
- 422 `FIRE_INFRASTRUCTURE_INVALID` with `issues[{code, path, message}]`.
- 404 for another organization.

### First version

The first version is seeded from the revision's equipment decisions (`PATCH /api/revisions/{id}/equipment-decisions`):
- Every **Confirmed/Retyped** candidate becomes an item: `kind` Equipment, category `WaterSource` for `water.*` and `Equipment` otherwise.
- Its position is the detected one, its rotation comes from `rotationY`, and `anchorId` is the IFC GlobalId.
- `capability.version` is taken from the newest active runtime, and `runtimeVersion` is set to that runtime.
- Exits and assembly points are not detected; the organization adds them. `geometry` is not seeded either: pin the accepted Geometry
  artifact as the editor does.

Later versions copy the newest version (items, geometry, runtimeVersion).

## Items

An item is a `fet3d.editor/1` object (`id, kind, label?, placement{floorId, position, rotation}, anchorId?, capability?, parameters?`) plus
`category`:

| category | kind | Rule |
|---|---|---|
| `Exit` | Goal | Lối thoát nạn; at least one is required to submit (`EXIT_REQUIRED`). |
| `AssemblyPoint` | Goal | Điểm tập kết. |
| `Equipment` | Equipment | Fixed equipment (alarm, extinguisher, door, sprinkler…), with a capability. |
| `WaterSource` | Equipment | Nguồn nước, with a capability. |

- `INFRASTRUCTURE_KIND_MISMATCH` when the category and kind do not match.
- `INFRASTRUCTURE_CAPABILITY_NOT_FIXED` for `item.towel` and `bait.belongings` (scenario items) and for `transport.elevator` (it comes from IFC).
- PUT runs the editor shape checks on the items. Validate and submit also check:
  - completeness and the capability contracts of `runtimeVersion`;
  - the pinned accepted Geometry (`geometry`);
  - floors, and anchors on their floor.

Issue paths are `$.items[i]…`, `$.geometry`, `$.runtimeVersion`.

## Thẩm định

A Submitted version appears in the shared expert queue as kind `Infrastructure` (see [expert-review.md](expert-review.md)); its id is the
infrastructure id.
- Approve or reject it with `If-Match` (its revision) and `Idempotency-Key`; reject needs a reason of 1-2000 characters. The first decision wins (412 for the next expert).
- `409 FIRE_INFRASTRUCTURE_NOT_PENDING` once it is decided.
- The submitter gets the web notification `FireInfrastructureApproved` or `FireInfrastructureRejected` (`fireInfrastructureId`, with the reason).

## Scenarios on an infrastructure

- `POST /api/scenarios/{id}/draft` needs `{revisionId, infrastructureId}`. Without `infrastructureId` it answers 400 `errors.infrastructureId`.
  - 422 `FIRE_INFRASTRUCTURE_NOT_APPROVED` when the version is not Approved.
  - 422 `FIRE_INFRASTRUCTURE_REVISION_MISMATCH` when it belongs to another revision.
  - The draft returns `infrastructureId`. One infrastructure carries many scenarios.
- The draft holds only scenario objects: Spawn, Hazard, Npc, BlockedElement, and the scenario items `item.towel` and `bait.belongings`.
  - Any Goal or other Equipment is refused: PUT and snapshot answer `422 SCENARIO_CONTAINS_INFRASTRUCTURE` (`errors` by path), and validate reports it as an issue at `$.objects[i]`.
- **Validate and snapshot work on the merged document**: the draft's objects followed by the infrastructure items (without `category`).
  - Issues on infrastructure items are reported at `$.infrastructure.items[i]`. An example is a capability version that the scenario's runtime does not publish.
  - The snapshot freezes this merged document in `state_snapshot` and records `infrastructureId` on the version (`GET /api/scenario-versions/{id}`).
  - So `scenarioHash`, the expert review, the legacy projections (`goal_config`…) and the package worker (`scenario.json`, `fet3d.package/1`) are unchanged: **Unity needs no change**. Two scenarios on the same infrastructure freeze the same equipment list.
- Such a draft also needs `audience`, `assumption` and `workingHours` (BE#215, see [plan-dossier.md](plan-dossier.md)); publishing needs Approved in-hours and off-hours situations on the infrastructure.
- Drafts created before BE#213 (`infrastructureId` null) keep their own objects and the previous rules.

## Web (FE) work

1. A revision screen "Hạ tầng PCCC":
   - list the versions, create one, edit the items in the 3D editor (place exits and assembly points);
   - validate, submit, and show the status and the rejection reason;
   - "tạo phiên bản mới" after a decision.
2. Scenario creation: choose an Approved infrastructure version (required). Remove exits and fixed equipment from the scenario palette, and show the infrastructure items read-only in the scenario editor.
3. Expert queue: add kind `Infrastructure`; `content.items` shows what to review.
4. Notifications: types `FireInfrastructureApproved` and `FireInfrastructureRejected`.

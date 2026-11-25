# Tình huống PC06, điều kiện phát hành và hồ sơ phương án (BE#215)

Flow 1 PC06, design §5 (`docs/flow1-pc06-target-design.md`); FR-PLAN-07, FR-PLAN-12. Migration `20261124120000_ScenarioPlanDossier`.

## Situation fields (scenario state, `fet3d.editor/1`)

| Field | Values | Mẫu PC06 |
|---|---|---|
| `audience` | `Evacuee` \| `Communications` \| `EvacuationGuide` \| `FirstResponse` | IV.1 b): người thoát nạn, thông tin liên lạc, hướng dẫn thoát nạn, chữa cháy ban đầu |
| `assumption` | `{time (1-100 chars), location (1-500), cause (1-500), peoplePresent (0-100000)}` | IV.1 a): giả định |
| `workingHours` | `InHours` \| `OffHours` | chú thích (3): trong hoặc ngoài giờ làm việc |

- PUT checks the values whenever the fields are present: a wrong value is `422 EDITOR_SCHEMA_INVALID`.
- On a draft built on a fire infrastructure (BE#213), validate reports `FIELD_REQUIRED` at `$.audience`, `$.workingHours` and `$.assumption[.field]` until all of them are filled, and snapshot answers 422. Drafts created before BE#213 do not need them.
- The fields are frozen with the version (`state_snapshot`). The package worker ignores unknown root fields of `scenario.json`; the game can show them before play.

## Publishing (FR-PLAN-07)

`POST /api/releases/{id}/publish` of a version built on a fire infrastructure also needs:
- that infrastructure `Approved`; otherwise 409 `FIRE_INFRASTRUCTURE_NOT_APPROVED`;
- at least one **Approved** situation (content review Approved, matching hash) with `workingHours=InHours`, and one with `OffHours`, on the **same infrastructure**. Otherwise the answer is **409 `PLAN_SCENARIO_COVERAGE_INCOMPLETE`** with `errors.workingHours` = the missing values (`["OffHours"]`, …).

Per infrastructure, as FR-PLAN-07 says ("mỗi hạ tầng"): one Building can have several revisions and infrastructures. Versions without an infrastructure publish as before.

## `GET /api/buildings/{id}/plan-dossier[?infrastructureId=]`

The "hồ sơ phương án trực quan" of a Building, in the order of Mẫu PC06. It is read-only, for the Building's OrganizationUser or PlatformAdmin; another organization gets 404.

It is built on the newest **Approved** infrastructure of the Building, or on `infrastructureId`:
- `infrastructureId` that is not an Approved version of this Building: 422 `FIRE_INFRASTRUCTURE_NOT_APPROVED`.
- No Approved infrastructure: `siteLayout` is null and `scenarios` is empty.

| Field | PC06 | Content |
|---|---|---|
| `header` | Đầu mẫu | `name`, `address` (Building location), `phone` (primary contact, else head of facility), `year` |
| `siteLayout` | I | `revisionId`, `infrastructureId`, `infrastructureVersion`, `exits[]`, `assemblyPoints[]` (infrastructure items) |
| `facility` | II | `facilityHeadName/Phone`, `onSiteForce`, `emergencyContacts` (BE#212); `equipment[{catalogKey, count}]` (II.5) and `waterSources[]` (II.6) from the infrastructure |
| `hazard` | III | `buildingType`, `riskGroup`, `totalFloors`, `floorAreaSquareMeters` |
| `scenarios[]` | IV.1 | The newest version of each scenario on the infrastructure, numbered 1, 2, 3… in creation order: `number, scenarioId, scenarioName, scenarioVersionId, versionNumber, audience, assumption, workingHours, reviewStatus (NotSubmitted / Submitted / Approved / Rejected), reviewedAt` |
| `coverage` | (3) | `inHours`, `offHours`: an Approved version (matching hash) on the infrastructure exists, the same rule as publishing; `complete`: both |
| `history[]` | V | Every infrastructure version of that revision: `id, version, status, submittedAt, decidedAt` |
| `signedPlanDocumentId` | | The signed PC06 (BE#212). The web view and any print are a **reference**, never the signed plan. |

Mục VI (theo dõi thực tập) comes from the training log (BE#218).

## Web (FE) work
1. Scenario editor:
   - pick the audience and in/off hours, and fill the assumption (time, location, cause, people present);
   - show the validate issues at `$.audience`, `$.workingHours` and `$.assumption`.
2. Publish: on `409 PLAN_SCENARIO_COVERAGE_INCOMPLETE`, say which of InHours or OffHours still needs an Approved situation.
3. A "Hồ sơ phương án" page per Building from `plan-dossier`:
   - sections in the order above, each opening the right place of the 3D model (exits and equipment through `placement`);
   - a "bản tham khảo" note on print.

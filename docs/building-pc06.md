# Building: loại công trình, thông tin cơ sở PC06 và nhóm nguy cơ (BE#212)

Flow 1 PC06, design §2 (`docs/flow1-pc06-target-design.md`). Migration `20261124100000_BuildingPc06Profile`.

## buildingType

- `Apartment` | `School` only, **required** on `POST /api/buildings` and `PUT /api/buildings/{id}`. Any other value (or none): `400 VALIDATION_ERROR` with `errors.buildingType`.
- Legacy data, mapped by the migration: "Chung cư"/"Apartment" → `Apartment`, "Trường học"/"School" → `School` (case and spaces ignored), anything else → `null`. A Building with `null` must choose a type on its next edit. A database CHECK keeps only the two values.

## Facility fields (Mẫu PC06 phần II), optional on create and update

| Field | Rule |
|---|---|
| `floorAreaSquareMeters` | Tổng diện tích sàn, > 0, at most 10,000,000, 2 decimals |
| `facilityHeadName` | Người đứng đầu cơ sở, at most 200 characters |
| `facilityHeadPhone` | 3–32 characters of digits, spaces, `+ - ( ) .`, at least 3 digits (114 is valid) |
| `onSiteForce` | `{teamSize 0–10000, leaderName, leaderPhone, availableInHours, availableOffHours}` |
| `emergencyContacts[]` | At most 20 of `{name (required), phone (required), role}` |

`PUT` replaces every editable field, so fields left out are cleared. Responses (`POST`, `PUT`, `GET /api/buildings/{id}`) return all fields plus `riskGroup` and `signedPlanDocumentId`; the list (`GET /api/buildings`) returns `riskGroup`.

## riskGroup (computed, never stored)

Phụ lục II Nghị định 105/2025/NĐ-CP, from `buildingType`, `totalFloors` and `floorAreaSquareMeters`. It is `null` without a type.

| Type | Group1 | Group2 | Otherwise |
|---|---|---|---|
| Apartment | ≥ 7 floors or ≥ 3,000 m² | 5–6 floors or 1,000 to < 3,000 m² | BelowThreshold |
| School | ≥ 5 floors or ≥ 3,000 m² | 3–4 floors or 1,500 to < 3,000 m² | BelowThreshold |

## Signed PC06 plan (private PDF)

The head of the facility approves the PC06 plan outside FET3D (Điều 15 Nghị định 105/2025/NĐ-CP). The Building's OrganizationUser or PlatformAdmin uploads the signed PDF:

1. `POST /api/buildings/{id}/signed-plan/upload-intent` with `{fileName, contentType: "application/pdf", sizeBytes ≤ 20 MB}` and an `Idempotency-Key`. Any other content type: 400 `errors.contentType`.
2. `PUT` the file to `uploadUrl` with the returned headers before `expiresAt` (30 minutes).
3. `POST /api/buildings/{id}/signed-plan/uploads/{uploadId}/complete`. The API checks the size and type and that the bytes start with `%PDF-` (otherwise `422 SIGNED_PLAN_NOT_PDF`). It computes the SHA-256, copies the file to a private object and sets `signedPlanDocumentId`, which moves the Building ETag. Completing again returns the same document.
4. `GET /api/buildings/{id}/signed-plan` answers `302` to a signed URL valid for 10 minutes, or 404 when there is no plan.

Errors: `409 SIGNED_PLAN_UPLOAD_MISSING | SIGNED_PLAN_UPLOAD_CHANGED | SIGNED_PLAN_UPLOAD_EXPIRED`, `503 SIGNED_PLAN_STORAGE_UNAVAILABLE`. Another organization gets 403 and nothing is public. Stored documents (`building_plan_documents`) cannot be changed or deleted; a new upload replaces the Building's pointer and keeps the old document as history.

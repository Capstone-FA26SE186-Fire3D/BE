# Avatar private S3

Implemented API routes, all requiring a Fire3D Bearer token:

| Method | Route | Request | Result |
| --- | --- | --- | --- |
| POST | `/api/me/avatar/upload` | Multipart `IFormFile`, field `file`; `If-Match` | BE streams to private staging and completes the same validated flow. Returns avatar and ETag. |
| POST | `/api/me/avatar/upload-intent` | `contentType`, `contentLength` | Server-owned staging key and signed PUT URL, valid for five minutes. |
| POST | `/api/me/avatar/complete` | `uploadId`, `If-Match` | Checks the staged object and returns a signed GET URL valid for five minutes and ETag. |
| GET | `/api/me/avatar` | none | Returns a fresh signed GET URL for the caller's own avatar. |
| DELETE | `/api/me/avatar` | `If-Match` | Atomically removes the reference, increments revision, writes audit/cleanup and returns 204/ETag. |

Only JPEG, PNG, and WebP are accepted: 5 MiB, at most 4096×4096 pixels, one frame. Complete verifies size/type/signature, then decodes the image and checks MIME. Reads and copy use the inspected source ETag. Keys are server-owned; arbitrary avatar URL/key is not accepted. The candidate final key/attempt/source ETag/lease is persisted **before** S3 copy; finalize adopts only the valid attempt, with profile revision, receipt and audit in one short transaction. No DB transaction is held across S3.

The decoder uses `SkiaSharp` and `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.153.1. This replaces the vulnerable ImageSharp 3.1.12 dependency without introducing ImageSharp 4's build-time license requirement. Do not suppress NuGet audit or omit Linux native assets from the deployment artifact. MIME, dimensions and frame count are checked before allocating bounded RGBA pixels; complete pixel decoding must return Success. PNG animation controls are also checked because the native codec can expose an APNG's default image as a static PNG. Multi-frame and truncated images are rejected before candidate reservation/copy; no upload/profile/replay/S3 authorization contract changes.

Dependency-repair evidence (2026-10-08): Release solution build and API publish succeeded with zero warnings/errors; NuGet audit including transitive packages reported no known vulnerabilities in all six projects. Windows decoder/rules/S3 mock regression passed 28 tests, zero failed/skipped, including all three formats, oversized dimensions, truncated pixels, mismatched decoded MIME and multi-frame PNG. The published artifact contains `runtimes/linux-x64/native/libSkiaSharp.so`. Linux execution and the last PostgreSQL regression rerun remain unverified: Docker stopped its fixtures and then returned 502/storage `metadata.db: input/output error` while host C: had no free space. Do not count a failed container startup or the interrupted wider run as passing acceptance; free disk space/restart Docker and rerun the gates before deployment.

Use the ETag from `GET /api/auth/me` for upload/complete/delete. Missing header is 428, malformed is 400, stale revision is 412. Within 24 hours of completion, replay the same upload ID **and original expected revision** to obtain a fresh signed URL/current revision if that avatar is still current. Different expected revision returns 412; replaced/deleted avatar returns `409 AVATAR_UPLOAD_SUPERSEDED`. Initial upload expiry does not invalidate an already committed receipt.

Additive migrations deliver `avatar_upload_intents` final/candidate/replay fields, `avatar_object_cleanups`, `users.avatar_storage_key` and `profile_revision`, preserving existing values. Cleanup worker/reconciler uses lease, retry and reference protection; protected objects remain queued for a later check. Staging expiry and abandoned candidates are recovered from durable rows. Delete does not promise immediate physical deletion from S3. Password storage is independent.

Personal username/profile ETag and organization PATCH are implemented separately; they are not missing APIs. Decoder, conditional copy and cleanup/replay have source/mock/isolated DB evidence, but production S3 IAM/CORS, upload races/timeouts and operational recovery require environment acceptance. Do not treat a successful build or signed URL as proof of S3 upload/delete.

Candidate reservation/finalize reject expired leases using the database clock after locking; finalize/delete serialize with personal profile mutation through lifecycle → user locks. Recovery regression covers audit rollback, expired candidate reconciliation, cleanup retry/reference protection and competing complete/delete on isolated PostgreSQL, including a restricted backend role. S3 reads handle fragmented streams and enforce the byte limit during reading. [Browser/Swagger test steps and evidence limits](avatar-manual-test.md).

The authenticated API returns a **signed S3 GET URL**. Open its entire `url` value, including signature query, without an API Bearer token until expiry. Copying `/api/me/avatar` instead still requires Bearer. Keep the bucket private; a signed URL is temporary read permission, not a public-object setting.

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

Use the ETag from `GET /api/auth/me` for upload/complete/delete. Missing header is 428, malformed is 400, stale revision is 412. Within 24 hours of completion, replay the same upload ID **and original expected revision** to obtain a fresh signed URL/current revision if that avatar is still current. Different expected revision returns 412; replaced/deleted avatar returns `409 AVATAR_UPLOAD_SUPERSEDED`. Initial upload expiry does not invalidate an already committed receipt.

Additive migrations deliver `avatar_upload_intents` final/candidate/replay fields, `avatar_object_cleanups`, `users.avatar_storage_key` and `profile_revision`, preserving existing values. Cleanup worker/reconciler uses lease, retry and reference protection; protected objects remain queued for a later check. Staging expiry and abandoned candidates are recovered from durable rows. Delete does not promise immediate physical deletion from S3. Password storage is independent.

Personal username/profile ETag and organization PATCH are implemented separately; they are not missing APIs. Decoder, conditional copy and cleanup/replay have source/mock/isolated DB evidence, but production S3 IAM/CORS, upload races/timeouts and operational recovery require environment acceptance. Do not treat a successful build or signed URL as proof of S3 upload/delete.

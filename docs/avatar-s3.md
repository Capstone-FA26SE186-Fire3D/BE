# Avatar private S3

Implemented API routes, all requiring a Fire3D Bearer token:

| Method | Route | Request | Result |
| --- | --- | --- | --- |
| POST | `/api/me/avatar/upload-intent` | `contentType`, `contentLength` | Server-owned staging key and signed PUT URL, valid for five minutes. |
| POST | `/api/me/avatar/complete` | `uploadId` | Checks the staged object and returns a signed GET URL valid for five minutes. |
| GET | `/api/me/avatar` | none | Returns a fresh signed GET URL for the caller's own avatar. |
| DELETE | `/api/me/avatar` | none | Removes the caller's avatar reference and returns 204. |

Only JPEG, PNG, and WebP are accepted. The limit is 5 MiB. The server creates the staging and final object keys; the client cannot supply either a storage key or arbitrary avatar URL. Completion requires the exact declared size, content type, and matching image magic bytes. The final S3 object is copied as private before its key is recorded in PostgreSQL.

The additive migration adds nullable `users.avatar_storage_key`, `users.profile_revision` with default 1, and `avatar_upload_intents`. It does not alter existing user values. The historical `password_hash` column is added with `IF NOT EXISTS`, because it was originally delivered by a SQL migration outside EF history.

This work does not complete the wider profile requirement: username, profile `If-Match`/ETag behaviour, organization profile update, durable retry of failed S3 cleanup, and certification of production S3 IAM/CORS remain pending. Unit tests and isolated PostgreSQL migration tests do not certify a real S3 provider.

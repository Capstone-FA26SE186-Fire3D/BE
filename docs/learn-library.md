# Learn CMS and Organization Library

Platform-administered public learning content (Learn) and reusable authoring assets for organizations (Library).
Migration: `20261011130000_AddLearnLibrary` (`Persistence/Sql/LearnLibrary.sql`). All writes go through the
SECURITY DEFINER gates `learn_admin_gate`, `learn_public_gate` and `library_gate`; the runtime role only needs EXECUTE.
No content is seeded.

## Learn model

- `learn_posts` (slug, `publication_status` Unpublished | Published | Hidden | Deleted, `published_version_id`, `revision_no`).
- `learn_post_versions` (Draft | Published, `content_schema_version = fet3d.learn/1`, kind Article | Tip | Video, plain-text
  title/summary, optional https cover image, canonical `blocks`). Published versions and their situation/source links are
  immutable (triggers reject UPDATE/DELETE); edits create a new version.
- `learn_situations` (slug, name, active flag), linked per version.
- `knowledge_sources` (Common | Organization visibility, Draft | Approved | Retired). A version may only cite Common sources,
  and publishing requires every cited source to be Approved (`409 LEARN_SOURCE_NOT_APPROVED`). The source registry API is
  part of #58; for now sources are inserted by operators.
- `learn_bookmarks` (Trainee, post).

### Content rules (`fet3d.learn/1`)

Blocks: `heading {text, level 2-4}`, `paragraph {text}`, `list {ordered, items[1-50]}`, `callout {tone, text}`, `video {url}`, `image {url, alt}`. Unknown fields or
block types are `422`. Text is plain text: any HTML-like tag is `MARKUP_NOT_ALLOWED` (a bare `<` such as `1 < 2` is fine).
Kind `Video` needs at least one video block. Video URLs must be https on the allowlist and are stored canonically:

| Provider | Accepted | Canonical |
|---|---|---|
| YouTube | `youtube.com/watch?v=`, `youtu.be/`, `youtube.com/shorts/`, `m.youtube.com` | `https://www.youtube.com/watch?v={id}` |
| TikTok | `tiktok.com/@user/video/{id}` | `https://www.tiktok.com/@user/video/{id}` |
| Facebook | `facebook.com/{page}/videos/{id}`, `facebook.com/watch/?v={id}` | `https://www.facebook.com/watch/?v={id}` |

Short links such as `fb.watch`, embed/iframe markup, credentials in the URL and other hosts are `422 LEARN_MEDIA_INVALID`.
`POST /api/admin/learn/media/validate {url}` returns `{provider, videoId, canonicalUrl}` for editors.

### Lifecycle

| From | Action | To |
|---|---|---|
| (new) | create post (`publish=false`) | Unpublished with Draft v1 |
| (new) | create post (`publish=true`) | Published |
| any but Deleted | publish a Draft version | Published (Hidden stays Hidden, with the new version current) |
| Published | hide | Hidden |
| Hidden | show | Published |
| any but Deleted | delete | Deleted |
| Deleted | restore | Hidden (never straight back to public) |

Public reads: Unpublished/Deleted → `404`, Hidden → `410 LEARN_POST_UNAVAILABLE`. Bookmarks of a non-Published post stay in the
list as `{postId, available:false, bookmarkedAt}` without title or summary; new bookmarks need a Published post.
`learn_rag_eligible(post, version)` is true only for the current published version of a Published or Hidden post; #58 RAG uses it.
Every state change enqueues a `PlatformCacheInvalidation` outbox event with `{scope:"learn", id, revision}`.

## Learn endpoints

| Method | Route | Auth | Notes |
|---|---|---|---|
| GET | `/api/learn/situations` | anonymous | Active situations. |
| GET | `/api/learn/posts?situation=&page=&pageSize=` | anonymous | Published only, newest first. |
| GET | `/api/learn/posts/{slug}` | anonymous | Published version only, no draft history. |
| GET | `/api/learn/bookmarks` | Trainee | Paged. |
| PUT/DELETE | `/api/learn/bookmarks/{postId}` | Trainee | Idempotent. |
| GET | `/api/admin/learn/posts?status=` | PlatformAdmin | All states with version list. |
| GET | `/api/admin/learn/posts/{id}` | PlatformAdmin | `ETag` = post revision. |
| POST | `/api/admin/learn/posts` | PlatformAdmin | `{slug, publish, version}` + Idempotency-Key → `201`. |
| POST | `/api/admin/learn/posts/{id}/versions` | PlatformAdmin | New Draft + Idempotency-Key. |
| GET/PUT | `/api/admin/learn/versions/{versionId}` | PlatformAdmin | PUT only on Draft, If-Match version revision. |
| POST | `/api/admin/learn/versions/{versionId}/publish` | PlatformAdmin | If-Match version revision. |
| POST | `/api/admin/learn/posts/{id}/hide\|show\|delete\|restore` | PlatformAdmin | If-Match post revision. |
| POST | `/api/admin/learn/situations` | PlatformAdmin | Idempotency-Key. |
| POST | `/api/admin/learn/media/validate` | PlatformAdmin | Preview of URL canonicalisation. |

## Organization Library

- `organization_library_items` (kind ScenarioTemplate | RubricSample | Equipment, unique upper-case `code`, `is_active`,
  `revision`). Items are never deleted (trigger); deactivate instead.
- `organization_library_versions` (Draft | Published, `name`, `payload`, `required_capabilities`). Published versions are
  immutable. Payload rules: ScenarioTemplate `{state}` is a complete `fet3d.editor/1` scenario state; RubricSample `{rubric}`
  uses the server metric allowlist (`RUBRIC_METRIC_UNSUPPORTED` otherwise); Equipment `{description, ...}` rejects unknown fields.
  Capabilities are distinct non-empty strings; they are not checked against Unity yet (no seeded capability list).

Organization users see only active items that have a Published version, and only Published versions. A deactivated item drops out of
lists and item reads, but its published versions stay readable by id so existing scenario snapshots keep resolving. Copying an
item into a scenario is done by the editor client and stays a copy; later library versions never change existing scenarios.

| Method | Route | Auth | Notes |
|---|---|---|---|
| GET | `/api/library/items?kind=` | OrganizationUser, PlatformAdmin | Active and published only. |
| GET | `/api/library/items/{id}` | OrganizationUser, PlatformAdmin | Includes `latestPublished`. |
| GET | `/api/library/versions/{id}` | OrganizationUser, PlatformAdmin | Published only for organizations. |
| GET/POST | `/api/admin/library/items` | PlatformAdmin | POST `{kind, code}` + Idempotency-Key. |
| GET/PATCH | `/api/admin/library/items/{id}` | PlatformAdmin | PATCH `{isActive}`, If-Match item revision. |
| POST | `/api/admin/library/items/{id}/versions` | PlatformAdmin | Draft + Idempotency-Key. |
| GET/PUT | `/api/admin/library/versions/{id}` | PlatformAdmin | PUT only on Draft, If-Match. |
| POST | `/api/admin/library/versions/{id}/publish` | PlatformAdmin | If-Match. |

Errors are ProblemDetails with `code` and `traceId`. Missing If-Match is `428`, malformed `400`, stale `412`. Lists default to 20
items, maximum 100. Library changes enqueue `PlatformCacheInvalidation` with `scope:"library"`.

## Tests

- `Fire3D.IfcTests/ContentValidationTests.cs`: URL canonicalisation, markup and strict-field rules, library payloads.
- `Fire3D.AuthTests/ContentPostgresTests.cs`: Learn lifecycle, public reads, bookmarks, RAG eligibility, immutability,
  outbox; library publish/deactivate visibility; HTTP ETag/If-Match, anonymous reads and validation errors.

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
| GET | `/api/learn/posts?search=&situation=&kind=&page=&pageSize=` | anonymous | Published post with a Published version only, newest first (publishedAt, id). `search` 2–100 chars on title+summary, case- and Vietnamese-diacritic-insensitive (`chay` finds `Cháy`, `duong` finds `Đường`; folded in SQL, no `unaccent` extension). Repeat `situation` (≤10 active slugs) and `kind` (Article/Tip/Video): values of one filter are OR, filters are AND. Filters run before paging; `total` is exact. Invalid values → 400 `VALIDATION_ERROR` with `errors`. (#79) |
| GET | `/api/learn/posts/{slug}` | anonymous | Published version only, no draft history. |
| GET | `/api/learn/bookmarks` | Trainee | Paged. |
| GET | `/api/learn/bookmarks/{postId}` | Trainee | `{postId, bookmarked}` for one post, without loading every bookmark (#86). |
| PUT/DELETE | `/api/learn/bookmarks/{postId}` | Trainee | Idempotent. |
| GET | `/api/admin/learn/posts?status=` | PlatformAdmin | All states. Each row adds `title`, `kind`, `latestVersionNumber`, `latestVersionStatus` (latest version, also for never-published posts), `draftVersionId`/`draftVersionNumber` (newest open Draft or null) and `versionCount` (#86). |
| GET | `/api/admin/learn/posts/{id}` | PlatformAdmin | `ETag` = post revision. |
| POST | `/api/admin/learn/posts` | PlatformAdmin | `{slug, publish, version}` + Idempotency-Key → `201`. |
| POST | `/api/admin/learn/posts/{id}/versions` | PlatformAdmin | New Draft + Idempotency-Key. |
| GET/PUT | `/api/admin/learn/versions/{versionId}` | PlatformAdmin | PUT only on Draft, If-Match version revision. |
| POST | `/api/admin/learn/versions/{versionId}/publish` | PlatformAdmin | If-Match version revision. |
| POST | `/api/admin/learn/posts/{id}/hide\|show\|delete\|restore` | PlatformAdmin | If-Match post revision. `show` re-applies the publish rules to the published version: every cited source still Approved Common (`409 LEARN_SOURCE_NOT_APPROVED`) and every situation active (`409 LEARN_SITUATION_INVALID`). |
| POST | `/api/admin/learn/situations` | PlatformAdmin | Idempotency-Key. |
| PATCH | `/api/admin/learn/situations/{id}` | PlatformAdmin | `{name?, description?, isActive?}` (at least one; empty description clears it). Slug never changes and situations are never deleted. Inactive situations leave public filters and block publish/show of versions using them; audited and cache-invalidated. |
| POST | `/api/admin/learn/media/validate` | PlatformAdmin | Preview of URL canonicalisation. |

## Policies clarified for #86

- Several Draft versions of one post may exist at once; publish names the exact version, and the others stay Draft.
- RAG eligibility (`learn_rag_eligible`) requires the current published version of a Published or Hidden post **and** every cited source still Approved Common. Retiring a source removes the post from retrieval immediately; Deleted is never eligible.
- Video blocks are stored as `{type:"video", provider, videoId, url}` from the allowlist only; content never holds HTML or iframes. A client that embeds builds the player URL itself from `provider` + `videoId` and allows only these hosts in its CSP `frame-src`: YouTube `https://www.youtube-nocookie.com/embed/{videoId}`, TikTok `https://www.tiktok.com/embed/v2/{videoId}`, Facebook `https://www.facebook.com/plugins/video.php?href={url-encoded url}`. Otherwise link out to `url`.

## Editor catalogs (#69)

- `GET /api/admin/knowledge-sources?visibility=&status=&search=&page=&pageSize=` (PlatformAdmin): `visibility` is optional: without it the registry lists every visibility so tenant sources can be reviewed; the editor source picker passes `visibility=Common`. `status` is Draft/Approved/Retired. `search` matches title, version label or jurisdiction, with wildcard characters taken literally. Results are ordered `createdAt DESC, id`, default 20, maximum 100. Each item has the source fields plus `canUseInDraft` (Common and Draft/Approved: a Learn draft may cite it) and `canPublishReference` (Common, Approved and inside its effective window: publish, Hidden and RAG accept it). Publish still re-checks the source in the backend. `GET /api/admin/knowledge-sources/{id}` returns the same flags, so a source chosen earlier can be reopened after a reload.
- `GET /api/admin/learn/situations?search=&isActive=&page=&pageSize=` (PlatformAdmin): paged catalog ordered by name, searching slug and name. The public `GET /api/learn/situations` is unchanged. Inactive situations stay readable in stored versions but cannot be chosen for new content (`LEARN_SITUATION_INVALID`). Nothing is seeded.

Migration: `20261012140000_AddCatalogLists` (read-only `catalog_list_gate`).

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
| GET/POST | `/api/admin/library/items` | PlatformAdmin | GET `kind`, `isActive`, `search` (part of the code, wildcards literal, max 80) before paging, ordered by code (#88). POST `{kind, code}` + Idempotency-Key. |
| GET/PATCH | `/api/admin/library/items/{id}` | PlatformAdmin | PATCH `{isActive}`, If-Match item revision. `code` (business key) and `kind` (fixes the payload schema) are immutable: create a new item instead. |
| POST | `/api/admin/library/items/{id}/versions` | PlatformAdmin | Draft + Idempotency-Key. |
| GET/PUT | `/api/admin/library/versions/{id}` | PlatformAdmin | PUT only on Draft, If-Match. |
| POST | `/api/admin/library/versions/{id}/publish` | PlatformAdmin | If-Match. Every `requiredCapabilities` entry must be offered by an active `runtime_compatibility_catalog` row, else `409 LIBRARY_CAPABILITY_UNSUPPORTED` (#88). |

Errors are ProblemDetails with `code` and `traceId`. Missing If-Match is `428`, malformed `400`, stale `412`. Lists default to 20
items, maximum 100. Library changes enqueue `PlatformCacheInvalidation` with `scope:"library"`.

## Tests

- `Fire3D.IfcTests/ContentValidationTests.cs`: URL canonicalisation, markup and strict-field rules, library payloads.
- `Fire3D.AuthTests/ContentPostgresTests.cs`: Learn lifecycle, public reads, bookmarks, RAG eligibility, immutability,
  outbox; library publish/deactivate visibility; HTTP ETag/If-Match, anonymous reads and validation errors.

## Idempotency on If-Match mutations (#57)

`PUT /api/admin/learn/versions/{id}`, `POST /api/admin/learn/versions/{id}/publish`, `POST /api/admin/learn/posts/{id}/{hide|show|delete|restore}`, `PATCH /api/admin/library/items/{id}`, `PUT /api/admin/library/versions/{id}` and `POST /api/admin/library/versions/{id}/publish` accept an optional `Idempotency-Key` next to the required `If-Match`. With a key, the first successful response is stored and replayed for the same key and input (resource, body, If-Match), even after the ETag moved, so a retried request after a lost response does not fail with 412. Another input under the same key is `409 IDEMPOTENCY_KEY_CONFLICT`; a malformed key is `400 IDEMPOTENCY_KEY_REQUIRED`. Without a key the behaviour is unchanged. Create routes keep their required key. The create body field is `publish` (boolean), not `publishImmediately`. Migration: `20261012170000_AddContentLifecycleKeys` (`content_lifecycle_gate`, wrapping the existing gates).

Library contract (#88): responses are typed in OpenAPI (`LibraryItemResponse`, `LibraryVersionResponse`, `LibraryItemPage` with `{items,total,page,pageSize}`). Invalid version content returns `422 CONTENT_INVALID` with `issues[]` (`code`, `path`, `message`), e.g. `RUBRIC_SCHEMA_VERSION_UNSUPPORTED` at `$.payload.rubric.schema_version`, `FIELD_UNKNOWN`, `STRING_INVALID`, `CAPABILITIES_INVALID`, `PROPERTIES_INVALID`. `payload.description` is optional for every kind (1–2000 characters when sent); clients must not require it. A draft records what it copied through the optional editor field `libraryRefs[]` (`versionId`, `kind`, `code?`), provenance only.

Roles: `/api/admin/learn/*` and `/api/admin/library/*` are PlatformAdmin only (OrganizationUser and Trainee get 403); `/api/library/*` is OrganizationUser and PlatformAdmin (Trainee 403); public Learn reads are anonymous.

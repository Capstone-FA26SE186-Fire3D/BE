# Fire3D Detailed API Specification

This document provides a detailed specification of the Fire3D APIs, including request and response body structures (DTOs).

## Authentication & Authorization
- **Scheme**: `Bearer <token>` (JWT)
- **Error Responses**: Validation errors or business logic errors return `400 Bad Request` or `409 Conflict` in RFC 7807 Problem Details format.

---

## 1. Authentication (`/api/auth`)

### 1.1. Login with Email/Password
**`POST /api/auth/login`** (Anonymous)
```json
// Request Body (LoginRequest)
{
  "email": "user@example.com",
  "password": "mySecurePassword123!"
}

// Response: 200 OK (TokenResponse)
{
  "accessToken": "ey...",
  "accessTokenExpiresAt": "2026-09-22T10:00:00Z",
  "refreshToken": "ey...",
  "refreshTokenExpiresAt": "2026-10-22T10:00:00Z",
  "user": {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "email": "user@example.com",
    "fullName": "John Doe",
    "role": "OrganizationUser",
    "organizationId": "5fa85f64-..."
  }
}
```

### 1.2. Login with Firebase (Google SSO)
**`POST /api/auth/login-firebase`** (Anonymous)
- Request: `IdToken` (string, the token returned by Firebase Client SDK).
- Response: Identical `TokenResponse` as Email Login.

### 1.3. Register
**`POST /api/auth/register`** (Anonymous)
```json
// Request Body (RegisterRequest)
{
  "email": "newuser@example.com",
  "password": "mySecurePassword123!",
  "fullName": "Jane Doe",
  "organizationName": "Fire Rescue Dept 1"
}
// Response: 201 Created (Empty body)
```

### 1.4. Password Recovery
**`POST /api/auth/forgot-password`** (Anonymous)
```json
// Request Body (ForgotPasswordRequest)
{
  "email": "user@example.com"
}
// Response: 202 Accepted (Empty body) - Email sent asynchronously via Outbox.
```

**`POST /api/auth/reset-password`** (Anonymous)
```json
// Request Body (ResetPasswordRequest)
{
  "oobCode": "firebase_oob_code_from_email_link",
  "newPassword": "newSecurePassword123!"
}
// Response: 200 OK (Empty body) - Revokes all existing sessions automatically.
```

---

## 2. Platform Administration (`/api/accounts`, `/api/organizations`)

### 2.1. Create Organization
**`POST /api/organizations`** (Requires `PlatformAdmin`)
```json
// Request (CreateOrganizationRequest)
{
  "name": "Fire Rescue Dept 1",
  "slug": "fire-rescue-1"
}

// Response: 201 Created (OrganizationResponse)
{
  "id": "...",
  "name": "Fire Rescue Dept 1",
  "slug": "fire-rescue-1",
  "isActive": true,
  "createdAt": "2026-09-22T10:00:00Z",
  "updatedAt": "2026-09-22T10:00:00Z"
}
```

### 2.2. Create Account (User)
**`POST /api/accounts`** (Requires `PlatformAdmin`)
```json
// Request (CreateAccountRequest)
{
  "email": "admin@dept1.com",
  "password": "TempPassword123!",
  "fullName": "Admin Dept1",
  "role": 1, // 0 = PlatformAdmin, 1 = OrganizationAdmin, 2 = OrganizationUser, 3 = Trainee
  "organizationId": "..."
}

// Response: 201 Created (ManagedAccountResponse)
{
  "id": "...",
  "email": "admin@dept1.com",
  "fullName": "Admin Dept1",
  "role": "OrganizationAdmin",
  "organizationId": "...",
  "isActive": true,
  "lastLoginAt": null,
  "createdAt": "2026-09-22T10:00:00Z",
  "updatedAt": "2026-09-22T10:00:00Z"
}
```

---

## 3. Buildings (`/api/buildings`)

### 3.1. Create Building
**`POST /api/buildings`** (Requires Auth)
```json
// Request (CreateBuildingRequest)
{
  "name": "Headquarters",
  "buildingType": "Office",
  "totalFloors": 10,
  "location": {
    "address": "123 Main St",
    "city": "Metropolis",
    "district": "Downtown",
    "latitude": 10.123,
    "longitude": 106.123
  },
  "contact": {
    "contactName": "Manager Bob",
    "contactRole": "Facility Manager",
    "phone": "555-1234",
    "email": "bob@hq.com",
    "isPrimary": true
  }
}

// Response: 201 Created (BuildingResponse)
// (Returns full Building object mirroring the request inputs + ID, timestamps, isActive)
```

---

## 4. IFC Processing (`/api/ifc-commands`, `/api/ifc-queries`)

### 4.1. Initiate IFC Upload
**`POST /api/buildings/{buildingId}/ifc`** (Requires Auth)
```json
// Request (InitiateIfcUploadRequest)
{
  "fileSizeBytes": 15000000,
  "originalFilename": "HQ_Model_v1.ifc",
  "versionLabel": "v1.0"
}
// Response: 200 OK (Contains S3 Presigned Upload URL and RevisionId)
```

### 4.2. Finalize IFC Upload
**`POST /api/revisions/{revisionId}/upload-complete`** (Requires Auth)
```json
// Request (FinalizeIfcUploadRequest)
{
  "objectKey": "s3-object-key-path",
  "fileSizeBytes": 15000000,
  "mimeType": "application/x-step",
  "sha256Hash": "a1b2c3d4...",
  "originalFilename": "HQ_Model_v1.ifc"
}
// Response: 204 No Content
```

### 4.3. Trigger Processing Job
**`POST /api/revisions/{revisionId}/process`** (Requires Auth)
Triggers the async background processing pipeline (Validation, Clean, NavMesh, GLB, etc.).
- Request: Empty body.
- Response: 202 Accepted (JobId).

---

## 5. VR Scenarios (`/api/scenarios`, `/api/scenario-drafts`)

### 5.1. Create Scenario
**`POST /api/scenarios`** (Requires Auth)
```json
// Request (CreateScenarioRequest)
{
  "buildingId": "...",
  "name": "Fire drill level 1"
}
// Response: 201 Created (Returns Guid of the new Scenario)
```

### 5.2. Create Scenario Draft
**`POST /api/scenarios/{scenarioId}/draft`** (Requires Auth)
```json
// Request (CreateScenarioDraftRequest)
{
  "revisionId": "..." // ID of the IFC revision the scenario is built on
}
// Response: 201 Created (Returns Guid of the Draft)
```

### 5.3. Update Scenario Draft (Logic Engine)
**`PUT /api/scenario-drafts/{draftId}`** (Requires Auth)
```json
// Request (UpdateScenarioDraftCommand parameters)
{
  "expectedVersion": 1, // Optimistic concurrency control
  "state": {
    "nodes": [
      { "id": "n1", "type": "FireNode", "position": [0,0,0] }
    ],
    "edges": []
  }
}
// Response: 200 OK (Returns new uint Version number)
```

### 5.4. Prepare Playtest Session
**`POST /api/scenarios/{scenarioId}/playtests`** (Requires Auth)
```json
// Request (PreparePlaytestRequest)
{
  "mode": 0, // 0 = Solo, 1 = Multiplayer
  "isVR": true,
  "draftId": "..." // Optional. Test a draft instead of published release.
}
// Response: 201 Created (Returns Guid of Playtest Session)
```

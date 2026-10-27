using System;
using System.Collections.Generic;
using System.Text;
namespace Fire3D.Domain.Enums;

public enum UserRole
{
    PlatformAdmin,
    OrganizationUser,
    Trainee
}

public enum UserGender
{
    Male,
    Female,
    Other,
    PreferNotToSay
}

public enum FileType
{
    IFC
}

public enum RevisionStatus
{
    Draft,
    Uploaded,
    Processing,
    NeedsFix,
    ReadyForScenario,
    ConfirmedForTraining,
    Rejected,
    Failed,
    Superseded
}

public enum ReviewAction
{
    ConfirmForTraining,
    Rejected
}

public enum ReleaseStatus
{
    Built,
    Published,
    Superseded,
    Revoked
}

public enum TrainingStatus
{
    Draft,
    Active,
    Closed,
    Archived
}

public enum QuarantineStatus
{
    Pending,
    Accepted,
    Rejected
}

public enum SessionMode
{
    Learn,
    Guided,
    Assessment
}

public enum SessionStatus
{
    Created,
    Launching,
    Running,
    Completed,
    CompletedWithSupersededRelease,
    ScenarioUnsurvivable,
    Aborted,
    Abandoned,
    Crashed
}

public enum AuditAction
{
    Upload,
    ConfirmForTraining,
    Reject,
    Publish,
    Revoke,
    Sync,
    Login,
    Logout,
    Download,
    Delete,
    Create,
    Update,
    Rollback,
    Grant,
    Resume,
    Payment,
    Support
}

// Kept only because historical migrations (and their designers) name these types; revision_processing_logs, the one table
// that used processing_step_enum and processing_step_status_enum, had no writer and is dropped, and nothing maps them now.
public enum ProcessingStep
{
    Quarantine,
    Parse,
    CleanGeometry,
    Decimate,
    GenNavMesh,
    GenHazardGrid,
    ExportGLB,
    PackageBundle
}

public enum ProcessingStepStatus
{
    Started,
    Success,
    Failed
}

// Các enum Phase 2 hiện đã có trong database.
public enum QuotationStatus
{
    Draft,
    Issued,
    Accepted,
    Expired,
    Cancelled
}

public enum PaymentRequestStatus
{
    Pending,
    Paid,
    Expired,
    Cancelled,
    Failed
}

public enum PaymentTransactionStatus
{
    Received,
    Verified,
    Rejected,
    Applied
}

public enum FeedbackStatus
{
    Submitted,
    Reviewed,
    Closed
}

public enum SupportTicketStatus
{
    Open,
    InProgress,
    Resolved,
    Closed
}

public enum SupportPriority
{
    Low,
    Normal,
    High,
    Urgent
}

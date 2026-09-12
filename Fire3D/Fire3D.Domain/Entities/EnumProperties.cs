using System;
using System.Collections.Generic;
using System.Text;

using Fire3D.Domain.Enums;

namespace Fire3D.Domain.Entities;

public partial class User
{
    public UserRole Role { get; set; }
}

public partial class Revision
{
    public FileType PrimaryType { get; set; } = FileType.IFC;
    public RevisionStatus Status { get; set; } = RevisionStatus.Draft;
}

public partial class SourceDocument
{
    public FileType FileType { get; set; } =
        global::Fire3D.Domain.Enums.FileType.IFC;

    public QuarantineStatus QuarantineStatus { get; set; } =
        global::Fire3D.Domain.Enums.QuarantineStatus.Pending;
}

public partial class RevisionProcessingLog
{
    public ProcessingStep Step { get; set; }
    public ProcessingStepStatus Status { get; set; }
}

public partial class RevisionReview
{
    public ReviewAction Action { get; set; }
}

public partial class Release
{
    public ReleaseStatus Status { get; set; } = ReleaseStatus.Built;
}

public partial class Training
{
    public TrainingStatus Status { get; set; } = TrainingStatus.Draft;
    public SessionMode Mode { get; set; } = SessionMode.Guided;
}

public partial class Session
{
    public SessionMode Mode { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Created;

    public ReleaseStatus? ContentStatusAtCompletion { get; set; }
}

public partial class AuditLog
{
    public AuditAction Action { get; set; }
}

public partial class Quotation
{
    public QuotationStatus Status { get; set; } = QuotationStatus.Draft;
}

public partial class PayosPaymentRequest
{
    public PaymentRequestStatus Status { get; set; } =
        PaymentRequestStatus.Pending;
}

public partial class PaymentTransaction
{
    public PaymentTransactionStatus Status { get; set; } =
        PaymentTransactionStatus.Received;
}

public partial class Feedback
{
    public FeedbackStatus Status { get; set; } = FeedbackStatus.Submitted;
}

public partial class SupportTicket
{
    public SupportPriority Priority { get; set; } = SupportPriority.Normal;

    public SupportTicketStatus Status { get; set; } =
        SupportTicketStatus.Open;
}
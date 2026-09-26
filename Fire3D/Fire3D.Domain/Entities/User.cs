using System;
using System.Collections.Generic;
using Fire3D.Domain.Enums;

namespace Fire3D.Domain.Entities;

public partial class User
{
    public Guid Id { get; set; }

    public Guid? OrganizationId { get; set; }

    public string Email { get; set; } = null!;

    public string? FirebaseUid { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? PasswordHash { get; set; }

    public string? FullName { get; set; }

    public string? AvatarStorageKey { get; set; }

    public long ProfileRevision { get; set; } = 1;

    public DateOnly? Dob { get; set; }

    public UserGender? Gender { get; set; }

    public string? PhoneNumber { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTime? EmailVerifiedAt { get; set; }

    public bool IsActive { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual ICollection<AnnotationSet> AnnotationSets { get; set; } = new List<AnnotationSet>();

    public virtual ICollection<Building> Buildings { get; set; } = new List<Building>();

    public virtual ICollection<Feedback> FeedbackReviewedByNavigations { get; set; } = new List<Feedback>();

    public virtual ICollection<Feedback> FeedbackSubmittedByNavigations { get; set; } = new List<Feedback>();

    public virtual Organization? Organization { get; set; }

    public virtual ICollection<PayosPaymentRequest> PayosPaymentRequests { get; set; } = new List<PayosPaymentRequest>();

    public virtual ICollection<Quotation> QuotationIssuedByNavigations { get; set; } = new List<Quotation>();

    public virtual ICollection<Quotation> QuotationRequestedByNavigations { get; set; } = new List<Quotation>();

    public virtual ICollection<Release> ReleasePublishedByNavigations { get; set; } = new List<Release>();

    public virtual ICollection<ReleaseQrCode> ReleaseQrCodes { get; set; } = new List<ReleaseQrCode>();

    public virtual ICollection<Release> ReleaseRevokedByNavigations { get; set; } = new List<Release>();

    public virtual ICollection<RevisionReview> RevisionReviews { get; set; } = new List<RevisionReview>();

    public virtual ICollection<Revision> Revisions { get; set; } = new List<Revision>();

    public virtual ICollection<ScenarioVersion> ScenarioVersions { get; set; } = new List<ScenarioVersion>();

    public virtual ICollection<Scenario> Scenarios { get; set; } = new List<Scenario>();

    public virtual ICollection<ServicePackage> ServicePackages { get; set; } = new List<ServicePackage>();

    public virtual ICollection<Session> Sessions { get; set; } = new List<Session>();

    public virtual ICollection<SourceDocument> SourceDocuments { get; set; } = new List<SourceDocument>();

    public virtual ICollection<SupportTicket> SupportTicketAssignedToNavigations { get; set; } = new List<SupportTicket>();

    public virtual ICollection<SupportTicket> SupportTicketCreatedByNavigations { get; set; } = new List<SupportTicket>();

    public virtual ICollection<Training> Training { get; set; } = new List<Training>();

    public virtual ICollection<UserDevice> UserDevices { get; set; } = new List<UserDevice>();
}

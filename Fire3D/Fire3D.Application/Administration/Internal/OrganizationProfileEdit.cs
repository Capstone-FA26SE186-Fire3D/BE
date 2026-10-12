using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Domain.Entities;

namespace Fire3D.Application.Administration.Internal;

/// <summary>Input rules shared by the OrganizationUser (/me) and PlatformAdmin ({id}) profile updates.</summary>
internal static class OrganizationProfileEdit
{
    internal static AuthResult<T>? Precheck<T>(string? ifMatch, UpdateOrganizationProfileRequest? request, out long revision)
    {
        if (!ProfileEtag.TryParse(ifMatch, out revision))
            return AuthResult<T>.Fail(string.IsNullOrWhiteSpace(ifMatch) ? "PRECONDITION_REQUIRED" : "VALIDATION_ERROR", "Send the organization ETag in If-Match.", string.IsNullOrWhiteSpace(ifMatch) ? 428 : 400);
        if (request is null)
            return AuthResult<T>.Fail("VALIDATION_ERROR", "Organization profile request is required.", 400);
        if (!request.NameSpecified && !request.AddressSpecified && !request.PhoneNumberSpecified)
            return AuthResult<T>.Fail("VALIDATION_ERROR", "Send at least one organization profile field.", 400);
        return null;
    }

    /// <summary>Applies the supplied fields over the stored profile; null when a supplied field is invalid.</summary>
    internal static (string Name, string? Address, string? Phone)? Resolve(Organization organization, UpdateOrganizationProfileRequest request)
    {
        var name = request.NameSpecified ? request.Name?.Trim() : organization.Name;
        var address = request.AddressSpecified ? request.Address?.Trim() : organization.Address;
        var phone = request.PhoneNumberSpecified
            ? request.PhoneNumber is null ? null : SelfRegistrationValidation.NormalizePhone(request.PhoneNumber)
            : organization.PhoneNumber;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255
            || (request.AddressSpecified && (string.IsNullOrWhiteSpace(address) || address.Length > 2000))
            || (request.PhoneNumberSpecified && (request.PhoneNumber is null || phone is null)))
            return null;
        return (name, address, phone);
    }

    internal static AuthResult<T> Invalid<T>() => AuthResult<T>.Fail("VALIDATION_ERROR", "Name, address, or phone number is invalid.", 400);

    internal static AuthResult<T> PhoneTaken<T>() => AuthResult<T>.Fail("ORGANIZATION_PHONE_EXISTS", "Số điện thoại đã được một tổ chức sử dụng.", 409,
        new Dictionary<string, string[]> { ["phoneNumber"] = ["Chọn số điện thoại khác cho tổ chức."] });

    internal static AuthResult<T> Stale<T>() => AuthResult<T>.Fail("PRECONDITION_FAILED", "The organization profile changed. Reload it and retry.", 412);
}

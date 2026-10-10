using Fire3D.Application.Authentication;
namespace Fire3D.Application.Reporting;
public sealed record AccountCount(string Role,bool IsActive,int Count);
public sealed record LifecycleCount(bool IsActive,int Count);
public sealed record ProcessingCount(string Kind,string Status,int Count);
public sealed record TicketCount(string Status,int Count);
public sealed record OrganizationCreated(int Buildings,int ProcessingJobs,int Tickets);
public sealed record PlatformCreated(int Accounts,int Organizations,int Buildings,int ProcessingJobs,int Tickets);
public sealed record PlatformOperations(DateTime AsOf,DateTime From,DateTime To,IReadOnlyDictionary<string,string> Definitions,IReadOnlyList<AccountCount> Accounts,IReadOnlyList<LifecycleCount> Organizations,IReadOnlyList<LifecycleCount> Buildings,IReadOnlyList<ProcessingCount> ProcessingJobs,IReadOnlyList<TicketCount> Tickets,PlatformCreated Created);
public sealed record OrganizationOperations(DateTime AsOf,DateTime From,DateTime To,IReadOnlyDictionary<string,string> Definitions,IReadOnlyList<LifecycleCount> Buildings,IReadOnlyList<ProcessingCount> ProcessingJobs,IReadOnlyList<TicketCount> Tickets,OrganizationCreated Created);
public interface IOperationsQueries
{
    Task<AuthResult<PlatformOperations>> Platform(Guid actor,Guid family,string? from,string? to,CancellationToken ct);
    Task<AuthResult<OrganizationOperations>> Organization(Guid actor,Guid family,string? from,string? to,CancellationToken ct);
}

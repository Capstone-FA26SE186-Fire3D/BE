using System.Security.Claims;
using Fire3D.Application.Administration;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Commands.CreateBuilding;
using Fire3D.Application.Buildings.Commands.SetBuildingActive;
using Fire3D.Application.Buildings.Commands.UpdateBuilding;
using Fire3D.Application.Buildings.Commands.UploadIfc;
using Fire3D.Application.Buildings.Queries.GetBuilding;
using Fire3D.Application.Buildings.Queries.ListBuildings;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/buildings")]
[Authorize] // Phải đăng nhập
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BuildingsController(ISender sender) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue("sub")!);
    private Guid OrganizationId => Guid.Parse(User.FindFirstValue("organization_id")!);

    /// <summary>
    /// Tạo mới một tòa nhà (có thể kèm Location và Contact)
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<BuildingResponse>> CreateBuilding(CreateBuildingRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateBuildingCommand(ActorId, OrganizationId, request), ct);
        return result.IsSuccess
            ? Created($"/api/buildings/{result.Value!.Id}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Lấy danh sách tòa nhà của tổ chức (có phân trang và tìm kiếm)
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PageResponse<BuildingSummaryResponse>>> ListBuildings([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, [FromQuery] bool? isActive = null, CancellationToken ct = default)
    {
        var filter = new BuildingFilter(page, pageSize, search, isActive);
        var result = await sender.Send(new ListBuildingsQuery(ActorId, OrganizationId, filter), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Lấy chi tiết thông tin một tòa nhà (bao gồm thông tin vị trí và liên hệ)
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BuildingResponse>> GetBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetBuildingQuery(ActorId, OrganizationId, id), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Cập nhật thông tin tòa nhà
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BuildingResponse>> UpdateBuilding(Guid id, UpdateBuildingRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateBuildingCommand(ActorId, OrganizationId, id, request), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Vô hiệu hóa (xóa mềm) tòa nhà
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<BuildingSummaryResponse>> DeleteBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new SetBuildingActiveCommand(ActorId, OrganizationId, id, false), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Upload file IFC để tạo bản vẽ mới (Revision) cho tòa nhà (Max 500MB)
    /// </summary>
    [HttpPost("{id:guid}/revisions")]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500MB Limit
    public async Task<ActionResult<RevisionResponse>> UploadIfc(Guid id, [FromForm] string versionLabel, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest("File is empty.");

        // Dùng /home/data trên Azure (persistent) hoặc Uploads/IFC trên local
        var isAzure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));
        var uploadsPath = isAzure 
            ? "/home/data/ifc-uploads" 
            : Path.Combine(Directory.GetCurrentDirectory(), "Uploads", "IFC");
            
        if (!Directory.Exists(uploadsPath)) Directory.CreateDirectory(uploadsPath);

        var extension = Path.GetExtension(file.FileName);
        var uniqueFileName = $"{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(uploadsPath, uniqueFileName);

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        await using var fileStream = new FileStream(filePath, FileMode.Create);
        await using var cryptoStream = new System.Security.Cryptography.CryptoStream(fileStream, sha256, System.Security.Cryptography.CryptoStreamMode.Write);
        
        await file.CopyToAsync(cryptoStream, ct);
        await cryptoStream.FlushFinalBlockAsync(ct);
        
        var hash = Convert.ToHexStringLower(sha256.Hash!);

        var command = new UploadIfcCommand(
            ActorId, 
            OrganizationId, 
            id, 
            versionLabel, 
            file.FileName, 
            file.Length, 
            filePath, 
            hash);
            
        var result = await sender.Send(command, ct);
        return result.IsSuccess
            ? Created($"/api/revisions/{result.Value!.Id}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Lấy danh sách các Revision (lịch sử upload bản vẽ) của một tòa nhà
    /// </summary>
    [HttpGet("{id:guid}/revisions")]
    public async Task<ActionResult<IReadOnlyList<RevisionResponse>>> ListRevisions(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Buildings.Queries.ListRevisions.ListRevisionsQuery(ActorId, OrganizationId, id), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}

using System;
using System.Threading.Tasks;
using Fmpostor.Server.WebAdmin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Http;

/// <summary>
///     Accepts in-game reports from the client addon and stores them for the web panel.
/// </summary>
[Route("/api/report")]
[ApiController]
public sealed class ReportController : ControllerBase
{
    private readonly ReportService _reportService;
    private readonly ILogger<ReportController> _logger;

    public ReportController(ReportService reportService, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    public sealed class ReportRequest
    {
        public string? ReporterName { get; set; }
        public string? ReporterPuid { get; set; }
        public string? ReporterFriendCode { get; set; }
        public string? TargetName { get; set; }
        public string? TargetPuid { get; set; }
        public string? TargetFriendCode { get; set; }
        public string? GameCode { get; set; }
        public string? Description { get; set; }
        public string? Reason { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] ReportRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { ok = false, error = "empty body" });
        }

        try
        {
            var entry = new ReportEntry
            {
                Time = DateTime.UtcNow,
                ReporterName = Clean(request.ReporterName, 64),
                ReporterFriendCode = Clean(request.ReporterFriendCode, 64),
                ReporterPuid = Clean(request.ReporterPuid, 64),
                ReporterIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
                GameCode = Clean(request.GameCode, 16),
                Description = Clean(request.Reason ?? request.Description ?? "举报", 512),
                ReportedPlayerName = Clean(request.TargetName, 64),
                ReportedPlayerFriendCode = Clean(request.TargetFriendCode, 64),
                ReportedPlayerPuid = Clean(request.TargetPuid, 64),
                Status = "pending",
            };

            if (entry.ReportedPlayerName.Length == 0 && entry.ReportedPlayerPuid.Length == 0)
            {
                return BadRequest(new { ok = false, error = "missing target" });
            }

            await _reportService.AddAsync(entry);
            _logger.LogInformation("[Report] {Reporter} reported {Target} in {Game}",
                entry.ReporterName, entry.ReportedPlayerName, entry.GameCode);

            return Ok(new { ok = true, id = entry.Id });
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[Report] submit failed");
            return StatusCode(500, new { ok = false });
        }
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}

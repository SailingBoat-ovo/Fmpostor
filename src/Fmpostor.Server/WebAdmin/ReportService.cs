using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class ReportEntry
{
    public int Id { get; set; }
    public DateTime Time { get; set; } = DateTime.UtcNow;
    public string ReporterName { get; set; } = "";
    public string ReporterFriendCode { get; set; } = "";
    public string ReporterPuid { get; set; } = "";
    public string ReporterIp { get; set; } = "";
    public string GameCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending | handled
}

internal class ReportStore
{
    public List<ReportEntry> Reports { get; set; } = new();
    public int NextId { get; set; } = 1;
}

/// <summary>
///     In-game player reports submitted with the /report command, persisted to
///     webadmin_reports.json and viewable from the web panel.
/// </summary>
public class ReportService
{
    private readonly string _filePath;
    private readonly ILogger<ReportService> _logger;
    private readonly object _lock = new();
    private ReportStore _store;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ReportService(IOptions<WebAdminConfig> config, ILogger<ReportService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.ReportFile);
        _store = new ReportStore();
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<ReportStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.NextId = data.NextId > 0 ? data.NextId : (_store.Reports.Count > 0 ? _store.Reports.Max(r => r.Id) + 1 : 1);
                    _logger.LogInformation("[Report] Loaded {Count} report(s) from {File}.", _store.Reports.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Report] Failed to load {File}, starting fresh.", _filePath);
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Report] Failed to save {File}.", _filePath);
            }
        }
    }

    public Task<ReportEntry> AddAsync(ReportEntry entry)
    {
        lock (_lock)
        {
            entry.Id = _store.NextId++;
            entry.Time = entry.Time == default ? DateTime.UtcNow : entry.Time;
            _store.Reports.Add(entry);

            // Cap at 2000 reports.
            if (_store.Reports.Count > 2000)
            {
                _store.Reports = _store.Reports.Skip(_store.Reports.Count - 2000).ToList();
            }

            Save();
            _logger.LogInformation("[Report] #{Id} from {Name} ({Fc}) in {Game}: {Desc}",
                entry.Id, entry.ReporterName, entry.ReporterFriendCode, entry.GameCode, entry.Description);
            return Task.FromResult(entry);
        }
    }

    public Task<List<ReportEntry>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_store.Reports.OrderByDescending(r => r.Time).ToList());
        }
    }

    public Task<bool> RemoveAsync(int id)
    {
        lock (_lock)
        {
            var removed = _store.Reports.RemoveAll(r => r.Id == id);
            if (removed > 0)
            {
                Save();
            }

            return Task.FromResult(removed > 0);
        }
    }

    public Task<bool> SetStatusAsync(int id, string status)
    {
        lock (_lock)
        {
            var report = _store.Reports.FirstOrDefault(r => r.Id == id);
            if (report == null)
            {
                return Task.FromResult(false);
            }

            report.Status = status == "handled" ? "handled" : "pending";
            Save();
            return Task.FromResult(true);
        }
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Ip { get; set; } = "";
}

internal class LogStore
{
    public List<LogEntry> Logs { get; set; } = new();
}

public class LogService
{
    private readonly string _filePath;
    private readonly ILogger<LogService> _logger;
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public LogService(ILogger<LogService> logger)
    {
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_logs.json");
        _logger = logger;
        LoadLogs();
    }

    private void LoadLogs()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonSerializer.Deserialize<LogStore>(json, JsonOptions);
                    if (data?.Logs != null)
                        _entries.AddRange(data.Logs);
                }
                catch
                {
                }
            }
        }
    }

    private void SaveLogs()
    {
        lock (_lock)
        {
            var store = new LogStore { Logs = _entries.TakeLast(500).ToList() };
            var json = JsonSerializer.Serialize(store, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
    }

    public void AddLog(string type, string detail, string ip)
    {
        lock (_lock)
        {
            _entries.Add(new LogEntry
            {
                Time = DateTime.UtcNow,
                Type = type,
                Detail = detail,
                Ip = ip,
            });
            SaveLogs();
        }
    }

    public Task<List<LogEntry>> GetLogsAsync()
    {
        lock (_lock)
        {
            var logs = _entries.OrderByDescending(l => l.Time).Take(100).ToList();
            return Task.FromResult(logs);
        }
    }
}

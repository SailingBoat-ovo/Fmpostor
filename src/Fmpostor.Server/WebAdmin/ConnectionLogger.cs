using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class ConnectLogEntry
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = "";  // connect, disconnect, error
    public string PlayerName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string GameCode { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class ConnectLogResponse
{
    public List<ConnectLogFileInfo> Files { get; set; } = new();
}

public class ConnectLogFileInfo
{
    public string FileName { get; set; } = "";
    public DateTime LastWrite { get; set; }
}

public class ConnectionLogger
{
    private readonly ILogger<ConnectionLogger> _logger;
    private readonly string _logDir;
    private readonly object _flushLock = new();
    private readonly List<ConnectLogEntry> _pending = new();
    private readonly System.Threading.Timer _flushTimer;
    private string _currentFile = "";

    private const long MaxLogFileBytes = 5 * 1024 * 1024;
    private const int MaxEntriesPerFile = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public ConnectionLogger(ILogger<ConnectionLogger> logger)
    {
        _logger = logger;
        _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "connectlog");
        Directory.CreateDirectory(_logDir);
        _currentFile = Path.Combine(_logDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");
        _flushTimer = new System.Threading.Timer(_ => Flush(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Log(string type, string playerName, string ipAddress, string gameCode, string detail = "")
    {
        var entry = new ConnectLogEntry
        {
            Time = DateTime.Now,
            Type = type,
            PlayerName = playerName,
            IpAddress = ipAddress,
            GameCode = gameCode,
            Detail = detail,
        };

        // Buffer in memory; the timer flushes to disk periodically so connect/disconnect
        // logging never blocks the single-threaded game packet processing path.
        lock (_flushLock)
        {
            _pending.Add(entry);
        }
    }

    public void Flush()
    {
        List<ConnectLogEntry> entries;
        lock (_flushLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            entries = new List<ConnectLogEntry>(_pending);
            _pending.Clear();
        }

        try
        {
            // Rotate when the current file grows too large.
            if (File.Exists(_currentFile) && new FileInfo(_currentFile).Length > MaxLogFileBytes)
            {
                _currentFile = Path.Combine(_logDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");
            }

            // Turbo-650: append-only JSONL — O(new entries) per flush instead of
            // read+parse+rewrite of the whole current file.
            JsonLines.Append(_currentFile, entries, JsonOptions);
        }
        catch
        {
            // Best-effort persistence; never crash the game thread on disk errors.
        }
    }

    public ConnectLogResponse GetLogFiles()
    {
        Flush();
        var result = new ConnectLogResponse();
        if (!Directory.Exists(_logDir)) return result;

        result.Files = Directory.GetFiles(_logDir, "*.json")
            .Select(f => new ConnectLogFileInfo
            {
                FileName = Path.GetFileName(f),
                LastWrite = File.GetLastWriteTime(f),
            })
            .OrderByDescending(f => f.LastWrite)
            .ToList();
        return result;
    }

    public List<ConnectLogEntry> GetLogContent(string fileName, string? filter)
    {
        Flush();
        // fileName comes from the panel API: strictly resolve it inside the log
        // directory (rejects ../, absolute paths and any separator tricks).
        var filePath = SafeFiles.ResolveInside(_logDir, fileName);
        if (filePath == null || !File.Exists(filePath)) return new();

        try
        {
            var entries = JsonLines.Read<ConnectLogEntry>(filePath, JsonOptions);
            if (!string.IsNullOrEmpty(filter))
                entries = entries.Where(e => e.Type == filter).ToList();
            entries.Reverse();
            return entries;
        }
        catch
        {
            return new();
        }
    }
}

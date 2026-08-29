using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Api.Games;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net.Inner.Objects;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class ChatLogEntry
{
    public DateTime Time { get; set; }
    public string Type { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Message { get; set; } = "";
    public int? TargetClientId { get; set; }
}

public class ChatLogList
{
    public List<ChatGameGroup> Games { get; set; } = new();
}

public class ChatGameGroup
{
    public string GameId { get; set; } = "";
    public List<ChatFileInfo> Files { get; set; } = new();
}

public class ChatFileInfo
{
    public string FileName { get; set; } = "";
    public string GameDir { get; set; } = "";
    public DateTime LastWrite { get; set; }
}

public class ChatService
{
    private readonly IGameManager _gameManager;
    private readonly ILogger<ChatService> _logger;
    private readonly string _chatLogDir;
    private readonly object _flushLock = new();
    private readonly Dictionary<string, List<ChatLogEntry>> _pending = new();
    private readonly System.Threading.Timer _flushTimer;

    private const int MaxEntriesPerFile = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ChatService(IGameManager gameManager, ILogger<ChatService> logger)
    {
        _gameManager = gameManager;
        _logger = logger;
        _chatLogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "chatlog");
        Directory.CreateDirectory(_chatLogDir);
        _flushTimer = new System.Threading.Timer(_ => Flush(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public async Task SendPublicMessageAsync(string gameCode, string message, string senderName)
    {
        foreach (var game in _gameManager.Games)
        {
            if (game.Code.Code != gameCode) continue;

            foreach (var player in game.Players)
            {
                if (player.Character != null)
                {
                    await player.Character.SendChatAsync($"[{senderName}] {message}");
                }
            }

            await SaveChatLogAsync(gameCode, game, "public", message, senderName, null);
            _logger.LogInformation("[Chat] Public message sent to game {Code}: {Msg}", gameCode, message);
            return;
        }
    }

    public async Task SendPrivateMessageAsync(string gameCode, List<int> targetClientIds, string message, string senderName)
    {
        foreach (var game in _gameManager.Games)
        {
            if (game.Code.Code != gameCode) continue;

            foreach (var player in game.Players)
            {
                if (player.Character != null && targetClientIds.Contains(player.Client.Id))
                {
                    await player.Character.SendChatToPlayerAsync($"[{senderName}] {message}", player.Character);

                    await SaveChatLogAsync(gameCode, game, "private", message, senderName, player.Client.Id);
                    _logger.LogInformation("[Chat] Private message sent to player {Name} in game {Code}: {Msg}",
                        player.Character.PlayerInfo?.PlayerName ?? player.Client.Name, gameCode, message);
                }
            }
            return;
        }
    }

    /// <summary>Returns the number of matching active games the message was sent to.</summary>
    public async Task<int> SendPublicMessageToGamesAsync(IEnumerable<string> gameCodes, string message, string senderName)
    {
        var codes = gameCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var delivered = 0;
        foreach (var game in _gameManager.Games)
        {
            if (!codes.Contains(game.Code.Code))
            {
                continue;
            }

            foreach (var player in game.Players)
            {
                if (player.Character != null)
                {
                    await player.Character.SendChatAsync($"[{senderName}] {message}");
                }
            }

            await SaveChatLogAsync(game.Code.Code, game, "public", message, senderName, null);
            _logger.LogInformation("[Chat] Public message sent to game {Code}: {Msg}", game.Code.Code, message);
            delivered++;
        }
        return delivered;
    }

    /// <summary>
    ///     Resolves flexible player keys (numeric clientIds, friend codes, player
    ///     names) to online client ids. Used by /api/chat/send so Agent-emitted
    ///     string targets do not crash JSON parsing.
    /// </summary>
    public List<int> ResolveClientIds(IEnumerable<string> keys)
    {
        var result = new List<int>();
        foreach (var raw in keys)
        {
            var key = (raw ?? "").Trim();
            if (key.Length == 0)
            {
                continue;
            }
            if (int.TryParse(key, out var id))
            {
                if (!result.Contains(id)) result.Add(id);
                continue;
            }
            foreach (var game in _gameManager.Games)
            {
                foreach (var player in game.Players)
                {
                    var client = player.Client;
                    if (client == null) continue;
                    var fc = client.FriendCode;
                    var nm = client.Name;
                    if ((fc != null && fc.Equals(key, StringComparison.OrdinalIgnoreCase))
                        || (nm != null && nm.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!result.Contains(client.Id)) result.Add(client.Id);
                    }
                }
            }
        }
        return result;
    }

    public async Task SendPrivateMessageToPlayersAsync(IEnumerable<int> targetClientIds, string message, string senderName)
    {
        var targets = targetClientIds.ToHashSet();
        foreach (var game in _gameManager.Games)
        {
            foreach (var player in game.Players)
            {
                if (player.Character == null || !targets.Contains(player.Client.Id))
                {
                    continue;
                }

                await player.Character.SendChatToPlayerAsync($"[{senderName}] {message}", player.Character);
                await SaveChatLogAsync(game.Code.Code, game, "private", message, senderName, player.Client.Id);
                _logger.LogInformation("[Chat] Private message sent to player {Name} in game {Code}: {Msg}",
                    player.Character.PlayerInfo?.PlayerName ?? player.Client.Name, game.Code.Code, message);
            }
        }
    }

    public async Task SavePlayerChatAsync(string gameCode, string playerName, string message)
    {
        foreach (var game in _gameManager.Games)
        {
            if (game.Code.Code == gameCode)
            {
                await SaveChatLogAsync(gameCode, game, "player", message, playerName, null);
                return;
            }
        }
    }

    private async Task SaveChatLogAsync(string gameCode, IGame game, string type, string message, string sender, int? targetId)
    {
        var entry = new ChatLogEntry
        {
            Time = DateTime.UtcNow,
            Type = type,
            Sender = sender,
            Message = message,
            TargetClientId = targetId,
        };

        var gameDirName = SanitizeFileName($"{gameCode}_{game.HostId}_{game.GameState}");
        var filePath = Path.Combine(
            Path.Combine(_chatLogDir, gameDirName),
            $"{DateTime.UtcNow:yyyy-MM-dd}.json");

        // Append to an in-memory buffer; the timer flushes to disk every few seconds.
        // This keeps file I/O off the single-threaded game packet processing path.
        lock (_flushLock)
        {
            if (!_pending.TryGetValue(filePath, out var list))
            {
                list = new List<ChatLogEntry>();
                _pending[filePath] = list;
            }

            list.Add(entry);
        }

        await Task.CompletedTask;
    }

    public void Flush()
    {
        List<KeyValuePair<string, List<ChatLogEntry>>> batches;
        lock (_flushLock)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batches = _pending.ToList();
            _pending.Clear();
        }

        foreach (var kv in batches)
        {
            try
            {
                var dir = Path.GetDirectoryName(kv.Key);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                List<ChatLogEntry> entries;
                if (File.Exists(kv.Key))
                {
                    try
                    {
                        var json = File.ReadAllText(kv.Key);
                        entries = JsonSerializer.Deserialize<List<ChatLogEntry>>(json, JsonOptions) ?? new List<ChatLogEntry>();
                    }
                    catch
                    {
                        entries = new List<ChatLogEntry>();
                    }
                }
                else
                {
                    entries = new List<ChatLogEntry>();
                }

                entries.AddRange(kv.Value);
                if (entries.Count > MaxEntriesPerFile)
                {
                    entries = entries.Skip(entries.Count - MaxEntriesPerFile).ToList();
                }

                var output = JsonSerializer.Serialize(entries, JsonOptions);
                var tmp = kv.Key + ".tmp";
                File.WriteAllText(tmp, output);
                File.Move(tmp, kv.Key, true);
            }
            catch
            {
                // Best-effort persistence; never crash the game thread on disk errors.
            }
        }
    }

    public ChatLogList GetChatLogFiles()
    {
        // Flush pending entries first so in-progress rooms appear immediately.
        Flush();

        var result = new ChatLogList();
        if (!Directory.Exists(_chatLogDir)) return result;

        foreach (var gameDir in Directory.GetDirectories(_chatLogDir))
        {
            var dirName = Path.GetFileName(gameDir);
            var files = Directory.GetFiles(gameDir, "*.json")
                .Select(f => new ChatFileInfo
                {
                    FileName = Path.GetFileName(f),
                    GameDir = dirName,
                    LastWrite = File.GetLastWriteTimeUtc(f),
                })
                .OrderByDescending(f => f.LastWrite)
                .ToList();

            result.Games.Add(new ChatGameGroup
            {
                GameId = dirName,
                Files = files,
            });
        }

        result.Games = result.Games.OrderByDescending(g => g.Files.FirstOrDefault()?.LastWrite).ToList();
        return result;
    }

    public async Task<List<ChatLogEntry>?> GetChatLogAsync(string gameDir, string fileName)
    {
        Flush();
        // Strict containment: gameDir/fileName come from the panel API.
        var dirPath = SafeFiles.ResolveInside(_chatLogDir, gameDir);
        var filePath = dirPath == null ? null : SafeFiles.ResolveInside(dirPath, fileName);
        if (filePath == null || !File.Exists(filePath)) return null;

        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<List<ChatLogEntry>>(json, JsonOptions);
    }

    /// <summary>
    ///     Recent chat lines from the newest log files, used as AI analysis data.
    /// </summary>
    public List<string> GetRecentForAi(int limit = 60)
    {
        Flush();

        var result = new List<string>();
        try
        {
            if (!Directory.Exists(_chatLogDir))
            {
                return result;
            }

            var files = Directory.GetDirectories(_chatLogDir)
                .SelectMany(d => Directory.GetFiles(d, "*.json")
                    .Select(f => new { Path = f, LastWrite = File.GetLastWriteTimeUtc(f) }))
                .OrderByDescending(x => x.LastWrite)
                .Take(3);

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file.Path);
                    var entries = JsonSerializer.Deserialize<List<ChatLogEntry>>(json, JsonOptions) ?? new List<ChatLogEntry>();
                    foreach (var entry in entries.TakeLast(20))
                    {
                        result.Add($"{entry.Time:MM-dd HH:mm:ss} {entry.Sender}: {entry.Message}");
                    }
                }
                catch
                {
                    // Skip unreadable files.
                }
            }
        }
        catch
        {
            // Best effort.
        }

        return result.TakeLast(limit).ToList();
    }

    private static string SanitizeFileName(string name)
    {
        return SafeFiles.SanitizeFileName(name);
    }
}

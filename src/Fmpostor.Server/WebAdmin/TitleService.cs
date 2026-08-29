using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Fmpostor.Api.Events;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.WebAdmin;

public class TitleEntry
{
    public string Name { get; set; } = "";
    public string HtmlFormat { get; set; } = "";
}

public class TitlePlayer
{
    public string FriendCode { get; set; } = "";
    public string Puid { get; set; } = "";
    public string Title { get; set; } = "";
}

public class TitlePlayerSettings
{
    public bool ShowTitle { get; set; } = true;
    public bool TitleWithBrackets { get; set; } = true;
    public string TitlePosition { get; set; } = "left";
}

public class TitleStore
{
    public bool EnableTitle { get; set; } = true;
    public bool TitleWithBrackets { get; set; } = true;
    public string TitlePosition { get; set; } = "left";
    public List<TitleEntry> Titles { get; set; } = new()
    {
        new TitleEntry { Name = "VIP", HtmlFormat = "<color=#FFD700>VIP</color>" },
        new TitleEntry { Name = "管理员", HtmlFormat = "<color=#FF0000>管理员</color>" },
    };
    public List<TitlePlayer> Players { get; set; } = new();
    public Dictionary<string, TitlePlayerSettings> PlayerSettings { get; set; } = new();
    public List<string> PendingRemovePlayers { get; set; } = new();
    public List<string> PendingUpdatePlayers { get; set; } = new();
}

/// <summary>
///     Player title system (ported from PlayerNamePlugin): /title commands,
///     titles applied when a player spawns. Configured via the web panel and
///     persisted to webadmin_titles.json (hot-reloaded).
/// </summary>
public class TitleService : IEventListener
{
    private readonly ILogger<TitleService> _logger;
    private readonly IEventManager _eventManager;
    private readonly string _filePath;
    private readonly object _lock = new();
    private TitleStore _store;
    private long _fileVersion = -1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public TitleService(IOptions<WebAdminConfig> config, ILogger<TitleService> logger, IEventManager eventManager)
    {
        _logger = logger;
        _eventManager = eventManager;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.Value.TitlesFile);
        _store = new TitleStore();
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                Save();
                return;
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<TitleStore>(json, JsonOptions);
                if (data != null)
                {
                    _store = data;
                    _store.Titles ??= new List<TitleEntry>();
                    _store.Players ??= new List<TitlePlayer>();
                    _store.PlayerSettings ??= new Dictionary<string, TitlePlayerSettings>();
                    _store.PendingRemovePlayers ??= new List<string>();
                    _store.PendingUpdatePlayers ??= new List<string>();
                    _fileVersion = GetFileVersion();
                    _logger.LogInformation("[Title] Loaded {Titles} title(s), {Players} player(s) from {File}.",
                        _store.Titles.Count, _store.Players.Count, _filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Title] Failed to load {File}, using defaults.", _filePath);
            }
        }
    }

    private void TryReload()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        var version = GetFileVersion();
        if (version == _fileVersion)
        {
            return;
        }

        _fileVersion = version;
        Load();
    }

    private long GetFileVersion()
    {
        var fi = new FileInfo(_filePath);
        return (fi.LastWriteTimeUtc.Ticks << 8) ^ fi.Length;
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_store, JsonOptions);
                File.WriteAllText(_filePath, json);
                _fileVersion = GetFileVersion();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Title] Failed to save {File}.", _filePath);
            }
        }
    }

    private static string GetPlayerIdentifier(IClientPlayer clientPlayer)
    {
        if (clientPlayer?.Client == null)
        {
            return string.Empty;
        }

        var friendCode = clientPlayer.Client.FriendCode;
        if (!string.IsNullOrEmpty(friendCode))
        {
            return friendCode;
        }

        var puid = clientPlayer.Client.Puid;
        if (!string.IsNullOrEmpty(puid))
        {
            return puid;
        }

        return clientPlayer.Client.Name ?? string.Empty;
    }

    private static string RemoveTitleFromName(string currentName, string titleHtml)
    {
        if (string.IsNullOrEmpty(currentName) || string.IsNullOrEmpty(titleHtml))
        {
            return currentName;
        }

        var result = currentName;
        if (result.StartsWith($"[{titleHtml}]", StringComparison.Ordinal))
        {
            result = result[$"[{titleHtml}]".Length..];
        }
        else if (result.EndsWith($"[{titleHtml}]", StringComparison.Ordinal))
        {
            result = result[..^$"[{titleHtml}]".Length];
        }
        else if (result.StartsWith(titleHtml, StringComparison.Ordinal))
        {
            result = result[titleHtml.Length..];
        }
        else if (result.EndsWith(titleHtml, StringComparison.Ordinal))
        {
            result = result[..^titleHtml.Length];
        }

        return result;
    }

    public async ValueTask ApplyTitleToPlayer(IClientPlayer clientPlayer)
    {
        if (clientPlayer?.Character == null || clientPlayer.Client == null)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                TryReload();
            }

            var identifier = GetPlayerIdentifier(clientPlayer);
            var player = _store.Players.FirstOrDefault(p =>
                (p.FriendCode != null && p.FriendCode == identifier) || (p.Puid != null && p.Puid == identifier));

            if (player == null || string.IsNullOrEmpty(player.Title))
            {
                return;
            }

            if (!_store.EnableTitle)
            {
                return;
            }

            var settings = GetPlayerSettings(identifier);
            if (!settings.ShowTitle)
            {
                return;
            }

            var titleConfig = _store.Titles.FirstOrDefault(t => t.Name == player.Title);
            var titleHtml = titleConfig?.HtmlFormat ?? player.Title;

            var currentName = clientPlayer.Character.PlayerInfo?.PlayerName ?? clientPlayer.Client.Name ?? string.Empty;
            var originalName = RemoveTitleFromName(currentName, titleHtml);
            if (string.IsNullOrEmpty(originalName))
            {
                originalName = clientPlayer.Client.Name ?? string.Empty;
            }

            var brackets = settings.TitleWithBrackets;
            var position = settings.TitlePosition == "right" ? "right" : "left";
            var newName = brackets
                ? (position == "left" ? $"[{titleHtml}]{originalName}" : $"{originalName}[{titleHtml}]")
                : (position == "left" ? $"{titleHtml}{originalName}" : $"{originalName}{titleHtml}");

            await clientPlayer.Character.SetNameAsync(newName);
            _logger.LogDebug("[Title] Applied title to {Name} -> {NewName}", originalName, newName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Title] Failed to apply title.");
        }
    }

    [EventListener]
    public async ValueTask OnPlayerSpawned(IPlayerSpawnedEvent e)
    {
        if (e.ClientPlayer?.Character == null || e.ClientPlayer.Client == null)
        {
            return;
        }

        await ApplyTitleToPlayer(e.ClientPlayer);
    }

    [EventListener]
    public async ValueTask OnGameEnded(IGameEndedEvent e)
    {
        await Task.Delay(1000);
        foreach (var player in e.Game.Players)
        {
            try
            {
                await ApplyTitleToPlayer(player);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Title] Re-apply failed for {Name}.", player.Client.Name);
            }
        }
    }

    // ========== Commands ==========

    public async ValueTask HandleChat(IPlayerChatEvent e, string message)
    {
        var playerControl = e.PlayerControl;
        if (playerControl == null)
        {
            return;
        }

        var parts = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await playerControl.SendChatToPlayerAsync("=== 称号系统帮助 ===\n/title help - 显示帮助\n/title set 1 true/false - 启用/禁用称号\n/title set 2 true/false - 称号是否带[]\n/title set 3 left/right - 称号位置", playerControl);
            return;
        }

        var sub = parts[1].ToLowerInvariant();
        switch (sub)
        {
            case "help":
                await playerControl.SendChatToPlayerAsync("=== 称号系统帮助 ===\n/title help - 显示帮助\n/title set 1 true/false - 启用/禁用称号\n/title set 2 true/false - 称号是否带[]\n/title set 3 left/right - 称号位置", playerControl);
                break;

            case "set":
                await HandleSetCommand(e, parts);
                break;

            default:
                await playerControl.SendChatToPlayerAsync("未知命令。使用 /title help 查看帮助", playerControl);
                break;
        }
    }

    private async ValueTask HandleSetCommand(IPlayerChatEvent e, string[] parts)
    {
        var playerControl = e.PlayerControl;
        if (playerControl == null || parts.Length < 4)
        {
            return;
        }

        var identifier = GetPlayerIdentifier(e.ClientPlayer);
        var settings = GetPlayerSettings(identifier);

        switch (parts[2])
        {
            case "1":
            {
                var value = parts[3].ToLowerInvariant();
                if (value == "true")
                {
                    lock (_lock) { TryReload(); _store.EnableTitle = true; Save(); }
                    await playerControl.SendChatToPlayerAsync("称号功能已启用", playerControl);
                    await ApplyTitleToPlayer(e.ClientPlayer);
                }
                else if (value == "false")
                {
                    lock (_lock) { TryReload(); _store.EnableTitle = false; Save(); }
                    await playerControl.SendChatToPlayerAsync("称号功能已禁用", playerControl);
                    var original = e.ClientPlayer.Client.Name ?? string.Empty;
                    await playerControl.SetNameAsync(original);
                }
                else
                {
                    await playerControl.SendChatToPlayerAsync("参数错误。使用 true 或 false", playerControl);
                }

                break;
            }

            case "2":
            {
                var value = parts[3].ToLowerInvariant();
                if (value == "true" || value == "false")
                {
                    settings.TitleWithBrackets = value == "true";
                    SavePlayerSettings(identifier, settings);
                    await playerControl.SendChatToPlayerAsync(value == "true" ? "称号将带[]包裹" : "称号将不带[]包裹", playerControl);
                    if (_store.EnableTitle)
                    {
                        await ApplyTitleToPlayer(e.ClientPlayer);
                    }
                }
                else
                {
                    await playerControl.SendChatToPlayerAsync("参数错误。使用 true 或 false", playerControl);
                }

                break;
            }

            case "3":
            {
                var value = parts[3].ToLowerInvariant();
                if (value == "left" || value == "right")
                {
                    settings.TitlePosition = value;
                    SavePlayerSettings(identifier, settings);
                    await playerControl.SendChatToPlayerAsync(value == "left" ? "称号将显示在名字前面" : "称号将显示在名字后面", playerControl);
                    if (_store.EnableTitle)
                    {
                        await ApplyTitleToPlayer(e.ClientPlayer);
                    }
                }
                else
                {
                    await playerControl.SendChatToPlayerAsync("参数错误。使用 left 或 right", playerControl);
                }

                break;
            }

            default:
                await playerControl.SendChatToPlayerAsync("未知设置类型。使用 /title help 查看帮助", playerControl);
                break;
        }
    }

    private TitlePlayerSettings GetPlayerSettings(string identifier)
    {
        lock (_lock)
        {
            TryReload();
            if (!_store.PlayerSettings.TryGetValue(identifier, out var settings))
            {
                settings = new TitlePlayerSettings
                {
                    ShowTitle = true,
                    TitleWithBrackets = _store.TitleWithBrackets,
                    TitlePosition = _store.TitlePosition,
                };
                _store.PlayerSettings[identifier] = settings;
            }

            return new TitlePlayerSettings
            {
                ShowTitle = settings.ShowTitle,
                TitleWithBrackets = settings.TitleWithBrackets,
                TitlePosition = settings.TitlePosition,
            };
        }
    }

    private void SavePlayerSettings(string identifier, TitlePlayerSettings settings)
    {
        lock (_lock)
        {
            TryReload();
            _store.PlayerSettings[identifier] = new TitlePlayerSettings
            {
                ShowTitle = settings.ShowTitle,
                TitleWithBrackets = settings.TitleWithBrackets,
                TitlePosition = settings.TitlePosition,
            };
            Save();
        }
    }

    // ========== Panel API ==========

    public TitleStore GetSnapshot()
    {
        lock (_lock)
        {
            TryReload();
            return new TitleStore
            {
                EnableTitle = _store.EnableTitle,
                TitleWithBrackets = _store.TitleWithBrackets,
                TitlePosition = _store.TitlePosition,
                Titles = _store.Titles.Select(t => new TitleEntry { Name = t.Name, HtmlFormat = t.HtmlFormat }).ToList(),
                Players = _store.Players.Select(p => new TitlePlayer { FriendCode = p.FriendCode, Puid = p.Puid, Title = p.Title }).ToList(),
                PlayerSettings = new Dictionary<string, TitlePlayerSettings>(_store.PlayerSettings),
            };
        }
    }

    public void UpdateGlobalSettings(bool? enableTitle, bool? titleWithBrackets, string? titlePosition)
    {
        lock (_lock)
        {
            TryReload();
            if (enableTitle.HasValue)
            {
                _store.EnableTitle = enableTitle.Value;
            }

            if (titleWithBrackets.HasValue)
            {
                _store.TitleWithBrackets = titleWithBrackets.Value;
            }

            if (!string.IsNullOrWhiteSpace(titlePosition))
            {
                _store.TitlePosition = titlePosition.Trim() == "right" ? "right" : "left";
            }

            Save();
        }
    }

    public void AddTitle(string name, string htmlFormat)
    {
        lock (_lock)
        {
            TryReload();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _store.Titles.RemoveAll(t => t.Name == name.Trim());
            _store.Titles.Add(new TitleEntry { Name = name.Trim(), HtmlFormat = htmlFormat ?? name.Trim() });
            Save();
        }
    }

    public void RemoveTitle(string name)
    {
        lock (_lock)
        {
            TryReload();
            _store.Titles.RemoveAll(t => t.Name == name);
            foreach (var player in _store.Players.Where(p => p.Title == name))
            {
                player.Title = "";
            }

            Save();
        }
    }

    public void SetPlayerTitle(string friendCode, string title)
    {
        lock (_lock)
        {
            TryReload();
            if (string.IsNullOrWhiteSpace(friendCode))
            {
                return;
            }

            var player = _store.Players.FirstOrDefault(p => p.FriendCode == friendCode.Trim());
            if (player == null)
            {
                player = new TitlePlayer { FriendCode = friendCode.Trim() };
                _store.Players.Add(player);
            }

            player.Title = title.Trim();
            Save();
        }
    }

    public void RemovePlayerTitle(string friendCode)
    {
        lock (_lock)
        {
            TryReload();
            _store.Players.RemoveAll(p => p.FriendCode == friendCode);
            Save();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;
using Fmpostor.Server.Net.Hazel;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

public class GameInfo
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int PlayerCount { get; set; }
    public string GameState { get; set; } = "";
    public bool IsPublic { get; set; }
    public string HostName { get; set; } = "";

    /// <summary>
    ///     True when this room currently honours the disconnect-reconnect
    ///     grace period, i.e. the feature is enabled and a round is running.
    /// </summary>
    public bool ReconnectArmed { get; set; }

    /// <summary>Number of dropped players whose slot is still reserved.</summary>
    public int ReconnectReservedSlots { get; set; }

    public List<PlayerInfo> Players { get; set; } = new();
}

public class PlayerInfo
{
    public int ClientId { get; set; }
    public string PlayerName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string ProductUserId { get; set; } = "";
    public string Fid { get; set; } = "";
    public bool IsHost { get; set; }
    public bool IsConnected { get; set; }
    public string Limbo { get; set; } = "";
    public int PingMs { get; set; }

    /// <summary>
    ///     True when <see cref="PingMs" /> is Hazel's floor value, i.e. the real round
    ///     trip is at least that (the panel shows "&gt;=50 ms").
    /// </summary>
    public bool PingIsUpperBound { get; set; }

    /// <summary>
    ///     True when <see cref="PingMs" /> is a real ICMP measurement (1ms resolution,
    ///     0 is a legitimate value for LAN/localhost) rather than Hazel's estimate.
    /// </summary>
    public bool PingIsExact { get; set; }
    public int DeltaPort { get; set; }
    public string GameVersion { get; set; } = "";
    public string Platform { get; set; } = "";
    public List<string> Mods { get; set; } = new();

    /// <summary>True while the connection is gone but the slot is reserved.</summary>
    public bool IsAwaitingReconnect { get; set; }

    /// <summary>When the reserved slot expires, UTC. Null when not awaiting.</summary>
    public DateTime? ReconnectExpiresAt { get; set; }

    /// <summary>Seconds left until the reserved slot expires.</summary>
    public int? ReconnectSecondsLeft { get; set; }
}

public class GameTrackerService
{
    private readonly IGameManager _gameManager;
    private readonly ILogger<GameTrackerService> _logger;
    private readonly LatencyProbeService _latencyProbe;

    public GameTrackerService(IGameManager gameManager, ILogger<GameTrackerService> logger, LatencyProbeService latencyProbe)
    {
        _gameManager = gameManager;
        _logger = logger;
        _latencyProbe = latencyProbe;
    }

    public int TotalGames => _gameManager.Games.Count();

    public int TotalPlayers => _gameManager.Games.Sum(g => g.PlayerCount);

    public IEnumerable<GameInfo> GetGames()
    {
        return _gameManager.Games.Select(g => new GameInfo
        {
            Code = g.Code.Code,
            DisplayName = g.DisplayName ?? "Unknown",
            PlayerCount = g.PlayerCount,
            GameState = g.GameState.ToString(),
            IsPublic = g.IsPublic,
            HostName = g.Host?.Client.Name ?? "Unknown",
            ReconnectArmed = g.IsReconnectArmed,
            ReconnectReservedSlots = g.ReconnectReservedSlots,
            Players = g.Players.Select(p => ToPlayerInfo(p)).ToList(),
        });
    }

    /// <summary>
    ///     Latency to show for a player: 0 when there is nothing honest to show
    ///     (no measurement yet, or the connection is gone because the slot is being
    ///     held open for a reconnect).
    /// </summary>
    /// <remarks>
    ///     取值顺序:
    ///     ① 游戏连接上的真实往返时间(HazelRttProbe) — 走的就是玩家实际的游戏链路, 最准;
    ///     ② ICMP 实测值 — 玩家那边没拦 ICMP 时可用;
    ///     ③ 回退 Hazel 的 DisplayPingMs。
    ///
    ///     为什么 ③ 只能兜底: Hazel 的平滑公式是
    ///     <c>_pingMs = Math.Max(50.0f, _pingMs * 0.7f + rt * 0.3f)</c>
    ///     (已用 IL 反编译确认 Impostor.Hazel 1.0.1 里就带这个 50 的下限),
    ///     所以 Hazel 永远不会报出 50 以下的数: 局域网 1~2ms 也只会显示 50。
    /// </remarks>
    internal int ConnectionPing(IClientPlayer p)
    {
        return ExactPing(p) ?? (p.IsAwaitingReconnect ? 0 : (p.Client.Connection is HazelConnection hazel ? hazel.DisplayPingMs : 0));
    }

    /// <summary>精确的延迟(①游戏连接实测 或 ②ICMP); 都没有时返回 null。</summary>
    private int? ExactPing(IClientPlayer p)
    {
        if (p.IsAwaitingReconnect)
        {
            return null;
        }

        if (p.Client.Connection is HazelConnection hazel
            && Fmpostor.Server.Net.Hazel.HazelRttProbe.TryGetRttMs(hazel.InnerConnection) is { } rtt)
        {
            return rtt;
        }

        return _latencyProbe.TryGetLatency(p.Client.Connection?.EndPoint?.Address);
    }

    private PlayerInfo ToPlayerInfo(IClientPlayer p)
    {        var info = new PlayerInfo
        {
            ClientId = p.Client.Id,
            PlayerName = p.Character?.PlayerInfo?.PlayerName ?? p.Client.Name,
            IpAddress = p.Client.Connection?.EndPoint?.Address?.ToString() ?? "Unknown",
            IsHost = p.IsHost,
            IsConnected = p.Client.Connection?.IsConnected ?? false,
            Limbo = p.Limbo.ToString(),
            PingMs = ConnectionPing(p),
            PingIsExact = ExactPing(p) != null,
            FriendCode = p.Client.FriendCode ?? string.Empty,
            ProductUserId = p.Client.ProductUserId ?? string.Empty,
            Fid = (p.Client.Items.TryGetValue("Fid", out var fid) ? fid?.ToString() : null) ?? string.Empty,
            DeltaPort = p.Client.Items.TryGetValue("DeltaPort", out var port) && port is int portInt ? portInt : 0,
            GameVersion = FormatGameVersion(p.Client.GameVersion),
            Platform = FormatPlatform(p.Client.PlatformSpecificData),
            Mods = ReactorModService.FormatMods(p.Client),
            IsAwaitingReconnect = p.IsAwaitingReconnect,
        };

        if (p.ReconnectExpiresAt is { } expiresAt)
        {
            info.ReconnectExpiresAt = expiresAt.UtcDateTime;
            info.ReconnectSecondsLeft = (int)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
        }

        return info;
    }

    /// <summary>
    ///     Shows the REAL game version sent by the client during the handshake
    ///     (e.g. 2026.7.15), appended with a human readable known-version label
    ///     when the version stamp is recognised. Unknown stamps are shown as-is,
    ///     nothing is ever guessed or hardcoded into the displayed value.
    /// </summary>
    private static string FormatGameVersion(GameVersion version)
    {
        var label = KnownVersionLabels.TryGetValue(version, out var name) ? name : null;
        return label != null ? $"{version} ({label})" : version.ToString();
    }

    /// <summary>
    ///     Version stamp -> release name knowledge base (kept in sync with
    ///     CompatibilityManager.DefaultSupportedVersions comments).
    /// </summary>
    private static readonly Dictionary<GameVersion, string> KnownVersionLabels = new()
    {
        [new GameVersion(2024, 3, 1)] = "2024.6.18",
        [new GameVersion(2024, 4, 1)] = "2024.8.13",
        [new GameVersion(2024, 4, 2)] = "2024.9.4",
        [new GameVersion(2024, 8, 10)] = "2024.10.29",
        [new GameVersion(2024, 8, 11)] = "16.0.0",
        [new GameVersion(2025, 4, 15)] = "16.0.5 / 16.1.0",
        [new GameVersion(2025, 7, 15)] = "17.0.0",
        [new GameVersion(2025, 9, 12)] = "17.0.1",
        [new GameVersion(2025, 10, 9)] = "17.1",
        [new GameVersion(2025, 11, 6)] = "17.1.1",
        [new GameVersion(2025, 12, 8)] = "17.1.2",
        [new GameVersion(2025, 11, 5)] = "17.2",
        [new GameVersion(2026, 1, 22)] = "17.2",
        [new GameVersion(2026, 2, 2)] = "17.2.2",
        [new GameVersion(2026, 1, 12)] = "17.3",
        [new GameVersion(2026, 3, 17)] = "17.3.1",
        [new GameVersion(2026, 3, 18)] = "17.4",
        [new GameVersion(2026, 4, 23)] = "17.4",
        [new GameVersion(2026, 7, 15)] = "18.0",
        [new GameVersion(2026, 7, 16)] = "18.0",
        [new GameVersion(2026, 7, 20)] = "19.0",
        [new GameVersion(2026, 7, 21)] = "19.0",
    };

    /// <summary>
    ///     Shows the REAL platform of the client (from the handshake platform
    ///     tag) with a friendly name; the client-declared platform name string
    ///     is only used as a fallback for unknown platforms, so spoofed/odd
    ///     names like "testname" can no longer replace the real platform.
    /// </summary>
    private static string FormatPlatform(PlatformSpecificData? data)
    {
        if (data == null)
        {
            return "Unknown";
        }

        return data.Platform switch
        {
            Platforms.StandaloneSteamPC => "Steam",
            Platforms.StandaloneEpicPC => "Epic",
            Platforms.StandaloneMac => "Mac",
            Platforms.StandaloneWin10 => "Win10",
            Platforms.StandaloneItch => "Itch",
            Platforms.IPhone => "iOS",
            Platforms.Android => "Android",
            Platforms.Switch => "Switch",
            Platforms.Xbox => "Xbox",
            Platforms.Playstation => "PS",
            _ => string.IsNullOrEmpty(data.PlatformName) ? "Unknown" : data.PlatformName,
        };
    }

    public PlayerInfo? FindPlayer(int clientId)
    {
        foreach (var game in _gameManager.Games)
        {
            var player = game.Players.FirstOrDefault(p => p.Client.Id == clientId);
            if (player != null)
            {
                return ToPlayerInfo(player);
            }
        }
        return null;
    }

    public async Task KickPlayerAsync(int clientId, string? reason = null)
    {
        foreach (var game in _gameManager.Games)
        {
            var player = game.Players.FirstOrDefault(p => p.Client.Id == clientId);
            if (player != null)
            {
                if (!string.IsNullOrEmpty(reason))
                {
                    await player.Client.DisconnectAsync(DisconnectReason.Custom, reason);
                }
                else
                {
                    await player.KickAsync();
                }
                _logger.LogInformation("Kicked player {PlayerName} (ID: {ClientId}). Reason: {Reason}",
                    player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name, clientId, reason ?? "No reason");
                return;
            }
        }
    }

    public async Task<int> KickPlayersAsync(IEnumerable<int> clientIds, string? reason = null)
    {
        var ids = clientIds.ToHashSet();
        var count = 0;
        foreach (var game in _gameManager.Games)
        {
            foreach (var player in game.Players)
            {
                if (!ids.Contains(player.Client.Id))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(reason))
                {
                    await player.Client.DisconnectAsync(DisconnectReason.Custom, reason);
                }
                else
                {
                    await player.KickAsync();
                }

                _logger.LogInformation("Kicked player {PlayerName} (ID: {ClientId}). Reason: {Reason}",
                    player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name, player.Client.Id, reason ?? "No reason");
                count++;
            }
        }

        return count;
    }

    public async Task BanPlayerAsync(int clientId)
    {
        foreach (var game in _gameManager.Games)
        {
            var player = game.Players.FirstOrDefault(p => p.Client.Id == clientId);
            if (player != null)
            {
                var ip = player.Client.Connection?.EndPoint?.Address?.ToString();
                var name = player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name;
                await player.BanAsync();
                _logger.LogInformation("Banned player {PlayerName} (ID: {ClientId}).",
                    name, clientId);
                return;
            }
        }
    }

    public async Task<int> KickPlayersByBanEntryAsync(string? ipAddress, string? playerName, string? friendCode = null)
    {
        var count = 0;
        foreach (var game in _gameManager.Games)
        {
            foreach (var player in game.Players)
            {
                var playerIp = player.Client.Connection?.EndPoint?.Address?.ToString();
                var playerNameMatch = !string.IsNullOrEmpty(playerName) &&
                    (player.Character?.PlayerInfo?.PlayerName?.Equals(playerName, System.StringComparison.OrdinalIgnoreCase) == true ||
                     player.Client.Name.Equals(playerName, System.StringComparison.OrdinalIgnoreCase));
                var ipMatch = !string.IsNullOrEmpty(ipAddress) &&
                    !string.IsNullOrEmpty(playerIp) &&
                    playerIp == ipAddress;
                var friendCodeMatch = !string.IsNullOrEmpty(friendCode) &&
                    !string.IsNullOrEmpty(player.Client.FriendCode) &&
                    player.Client.FriendCode.Equals(friendCode, StringComparison.OrdinalIgnoreCase);

                if (ipMatch || playerNameMatch || friendCodeMatch)
                {
                    await player.Client.DisconnectAsync(DisconnectReason.Custom,
                        "You have been banned from this server.");
                    _logger.LogInformation("Disconnected banned player {PlayerName} (IP: {Ip}).",
                        player.Character?.PlayerInfo?.PlayerName ?? player.Client.Name, playerIp);
                    count++;
                }
            }
        }
        return count;
    }
}

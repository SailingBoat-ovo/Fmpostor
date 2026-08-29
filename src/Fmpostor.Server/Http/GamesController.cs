using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fmpostor.Api.Config;
using Fmpostor.Api.Games;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;
using Fmpostor.Server.Extensions;
using Fmpostor.Server.WebAdmin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.Http;

/// <summary>
/// This controller has method to get a list of public games, join by game and create new games.
/// </summary>
[Route("/api/games")]
[ApiController]
public sealed class GamesController : ControllerBase
{
    private readonly IGameManager _gameManager;
    private readonly ListingManager _listingManager;
    private readonly HostServer _hostServer;
    private readonly PlayerIdentityService _identityService;
    private readonly WebAdminConfig _webAdminConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="GamesController"/> class.
    /// </summary>
    /// <param name="gameManager">GameManager containing a list of games.</param>
    /// <param name="listingManager">ListingManager responsible for filtering.</param>
    /// <param name="serverConfig">Fmpostor configuration section containing the public ip address of this server.</param>
    public GamesController(IGameManager gameManager, ListingManager listingManager, IOptions<ServerConfig> serverConfig, PlayerIdentityService identityService, IOptions<WebAdminConfig> webAdminConfig)
    {
        _gameManager = gameManager;
        _listingManager = listingManager;
        _identityService = identityService;
        _webAdminConfig = webAdminConfig.Value;
        var config = serverConfig.Value;
        _hostServer = HostServer.From(IPAddress.Parse(config.ResolvePublicIp()), config.PublicPort);
    }

    /// <summary>
    ///     Resolves the delta UDP port allocated for this client during the HTTP
    ///     token exchange, so the client connects to a dedicated port that exactly
    ///     identifies its auth session. Falls back to the public port.
    /// </summary>
    private ushort GetDeltaPort()
    {
        var ip = ClientIpHelper.GetClientIp(HttpContext.Request, _webAdminConfig.TrustAllProxies, _webAdminConfig.TrustedProxies);
        if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out var addr))
        {
            var identity = _identityService.Lookup(addr);
            if (identity != null && identity.Port > 0)
            {
                return (ushort)identity.Port;
            }
        }

        return (ushort)_hostServer.Port;
    }

    /// <summary>
    /// Get a list of active games.
    /// </summary>
    /// <param name="mapId">Maps that are requested.</param>
    /// <param name="lang">Preferred chat language.</param>
    /// <param name="numImpostors">Amount of impostors. 0 is any.</param>
    /// <param name="authorization">Authorization header containing the matchmaking token.</param>
    /// <returns>An array of game listings.</returns>
    [HttpGet]
    public IActionResult Index(int mapId, GameKeywords lang, int numImpostors, [FromHeader] AuthenticationHeaderValue authorization)
    {
        // NOTE: this method is no longer used by Among Us 16.0.0 and is only kept for backwards compatibility
        if (authorization.Scheme != "Bearer" || authorization.Parameter == null)
        {
            return BadRequest();
        }

        var token = JsonSerializer.Deserialize<TokenController.Token>(Convert.FromBase64String(authorization.Parameter));
        if (token == null)
        {
            return BadRequest();
        }

        var clientVersion = new GameVersion(token.Content.ClientVersion);
        var deltaPort = GetDeltaPort();

        var listings = _listingManager.FindListings(HttpContext, mapId, numImpostors, lang, clientVersion);
        return Ok(listings.Select(g => GameListing.From(g, deltaPort)));
    }

    /// <summary>
    /// Get the address a certain game is hosted at.
    /// </summary>
    /// <param name="gameId">The id of the game that should be retrieved.</param>
    /// <returns>The server this game is hosted on.</returns>
    [HttpPost]
    public IActionResult Post(int gameId)
    {
        // NOTE: this method is no longer used by Among Us 16.0.0 and is only kept for backwards compatibility
        var code = new GameCode(gameId);
        var game = _gameManager.Find(code);

        // If the game was not found, print an error message.
        if (game == null)
        {
            return NotFound(new MatchmakerResponse(new MatchmakerError(DisconnectReason.GameNotFound)));
        }

        return Ok(HostServer.From(game.PublicIp.Address, GetDeltaPort()));
    }

    /// <summary>
    /// Get the address to host a new game on.
    /// </summary>
    /// <returns>The address of this server.</returns>
    [HttpPut]
    public IActionResult Put()
    {
        return Ok(HostServer.From(_hostServer.Ip, GetDeltaPort()));
    }

    [HttpGet("{gameId}")]
    public IActionResult Show([FromRoute] int gameId)
    {
        var code = new GameCode(gameId);
        var game = _gameManager.Find(code);

        // If the game was not found, print an error message.
        if (game == null)
        {
            return NotFound(new FindGameByCodeResponse(new MatchmakerError(DisconnectReason.GameNotFound)));
        }

        return Ok(new FindGameByCodeResponse(GameListing.From(game, GetDeltaPort())));
    }

    [HttpGet("filtered")]
    public IActionResult ShowFilteredLobbies()
    {
        // TODO: implement this stub
        var response = new
        {
            games = Array.Empty<GameListing>(),
            metadata = new
            {
                allGamesCount = _gameManager.Games.Count(),
                matchingGamesCount = 0,
            },
        };

        return Ok(response);
    }

    private static uint ConvertAddressToNumber(IPAddress address)
    {
#pragma warning disable CS0618 // Among Us only supports IPv4
        return (uint)address.Address;
#pragma warning restore CS0618
    }

    private class HostServer
    {
        [JsonPropertyName("Ip")]
        public required long Ip { get; init; }

        [JsonPropertyName("Port")]
        public required ushort Port { get; init; }

        public static HostServer From(IPAddress ipAddress, ushort port)
        {
            return new HostServer
            {
                Ip = ConvertAddressToNumber(ipAddress),
                Port = port,
            };
        }

        public static HostServer From(long ip, ushort port)
        {
            return new HostServer
            {
                Ip = ip,
                Port = port,
            };
        }

        public static HostServer From(IPEndPoint endPoint)
        {
            return From(endPoint.Address, (ushort)endPoint.Port);
        }
    }

    private class MatchmakerResponse
    {
        [SetsRequiredMembers]
        public MatchmakerResponse(MatchmakerError error)
        {
            Errors = new[] { error };
        }

        [JsonPropertyName("Errors")]
        public required MatchmakerError[] Errors { get; init; }
    }

    private class MatchmakerError
    {
        [SetsRequiredMembers]
        public MatchmakerError(DisconnectReason reason)
        {
            Reason = reason;
        }

        [JsonPropertyName("Reason")]
        public required DisconnectReason Reason { get; init; }
    }

    private class FindGameByCodeResponse
    {
        [SetsRequiredMembers]
        public FindGameByCodeResponse(MatchmakerError error) => (Errors, Game) = (new[] { error }, null);

        [SetsRequiredMembers]
        public FindGameByCodeResponse(GameListing game) => (Errors, Game) = (null, game);

        [JsonPropertyName("Errors")]
        public required MatchmakerError[]? Errors { get; init; }

        [JsonPropertyName("Game")]
        public required GameListing? Game { get; init; }
    }

    private class GameListing
    {
        [JsonPropertyName("IP")]
        public required uint Ip { get; init; }

        [JsonPropertyName("Port")]
        public required ushort Port { get; init; }

        [JsonPropertyName("GameId")]
        public required int GameId { get; init; }

        [JsonPropertyName("PlayerCount")]
        public required int PlayerCount { get; init; }

        [JsonPropertyName("HostName")]
        public required string HostName { get; init; }

        [JsonPropertyName("TrueHostName")]
        public required string TrueHostName { get; init; }

        [JsonPropertyName("HostPlatformName")]
        public required string HostPlatformName { get; init; }

        [JsonPropertyName("Platform")]
        public required Platforms Platform { get; init; }

        [JsonPropertyName("QuickChat")]
        public required QuickChatModes QuickChat { get; init; }

        [JsonPropertyName("Age")]
        public required int Age { get; init; }

        [JsonPropertyName("MaxPlayers")]
        public required int MaxPlayers { get; init; }

        [JsonPropertyName("NumImpostors")]
        public required int NumImpostors { get; init; }

        [JsonPropertyName("MapId")]
        public required MapTypes MapId { get; init; }

        [JsonPropertyName("Language")]
        public required GameKeywords Language { get; init; }

        [JsonPropertyName("Options")]
        public required string Options { get; init; }

        public static GameListing From(IGame game)
        {
            return From(game, (ushort)game.PublicIp.Port);
        }

        public static GameListing From(IGame game, ushort port)
        {
            var platform = game.Host?.Client.PlatformSpecificData;

            return new GameListing
            {
                Ip = ConvertAddressToNumber(game.PublicIp.Address),
                Port = port,
                GameId = game.Code,
                PlayerCount = game.PlayerCount,
                HostName = game.DisplayName ?? game.Host?.Client.Name ?? "Unknown host",
                TrueHostName = game.DisplayName ?? game.Host?.Client.Name ?? "Unknown host",
                HostPlatformName = platform?.PlatformName ?? string.Empty,
                Platform = platform?.Platform ?? Platforms.Unknown,
                QuickChat = game.Host?.Client.ChatMode ?? QuickChatModes.QuickChatOnly,
                Age = 0,
                MaxPlayers = game.Options.MaxPlayers,
                NumImpostors = game.Options.NumImpostors,
                MapId = game.Options.Map,
                Language = game.Options.Keywords,
                Options = game.Options.ToBase64String(),
            };
        }
    }
}

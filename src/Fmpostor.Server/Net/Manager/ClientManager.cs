using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Config;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Manager;
using Impostor.Hazel;
using Fmpostor.Server.Events.Client;
using Fmpostor.Server.Net.Factories;
using Fmpostor.Server.WebAdmin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.Net.Manager
{
    internal partial class ClientManager
    {
        private readonly ILogger<ClientManager> _logger;
        private readonly IEventManager _eventManager;
        private readonly ConcurrentDictionary<int, ClientBase> _clients;
        private readonly ICompatibilityManager _compatibilityManager;
        private readonly CompatibilityConfig _compatibilityConfig;
        private readonly IClientFactory _clientFactory;
        private readonly PlayerIdentityService _identityService;
        private readonly DeltaPortPoolService _portPool;
        private int _idLast;

        public ClientManager(ILogger<ClientManager> logger, IEventManager eventManager, IClientFactory clientFactory, ICompatibilityManager compatibilityManager, IOptions<CompatibilityConfig> compatibilityConfig, PlayerIdentityService identityService, DeltaPortPoolService portPool)
        {
            _logger = logger;
            _eventManager = eventManager;
            _clientFactory = clientFactory;
            _identityService = identityService;
            _portPool = portPool;
            _clients = new ConcurrentDictionary<int, ClientBase>();
            _compatibilityManager = compatibilityManager;
            _compatibilityConfig = compatibilityConfig.Value;

            if (_compatibilityConfig.AllowFutureGameVersions
                || _compatibilityConfig.AllowHostAuthority
                || _compatibilityConfig.AllowVersionMixing)
            {
                _logger.LogWarning("One or more compatibility options were enabled, please mention these when seeking support:");

                if (_compatibilityConfig.AllowFutureGameVersions)
                {
                    _logger.LogWarning("AllowFutureGameVersions, which allows future Among Us versions to connect that were unknown at the time this Impostor was built");
                }

                if (_compatibilityConfig.AllowHostAuthority)
                {
                    _logger.LogWarning("AllowHostAuthority, which allows game hosts to control more game features, but it uses less well tested code on the client, which causes some bugs");
                }

                if (_compatibilityConfig.AllowVersionMixing)
                {
                    _logger.LogWarning("AllowVersionMixing, which allows players to join games created on different game versions that they may not be 100% compatible with");
                }
            }
        }

        public IEnumerable<ClientBase> Clients => _clients.Values;

        public int NextId()
        {
            var clientId = Interlocked.Increment(ref _idLast);

            if (clientId < 1)
            {
                // Super rare but reset the _idLast because of overflow.
                _idLast = 0;

                // And get a new id.
                clientId = Interlocked.Increment(ref _idLast);
            }

            return clientId;
        }

        public async ValueTask RegisterConnectionAsync(IHazelConnection connection, string name, GameVersion clientVersion, Language language, QuickChatModes chatMode, PlatformSpecificData? platformSpecificData, int deltaPort = 0)
        {
            var versionCompare = _compatibilityManager.CanConnectToServer(clientVersion);
            if (versionCompare == ICompatibilityManager.VersionCompareResult.ServerTooOld && _compatibilityConfig.AllowFutureGameVersions && platformSpecificData != null)
            {
                _logger.LogWarning("Client connected using future version: {clientVersion} ({version}). Unsupported, continue at your own risk.", clientVersion.Value, clientVersion.ToString());
            }
            else if (versionCompare != ICompatibilityManager.VersionCompareResult.Compatible || platformSpecificData == null)
            {
                _logger.LogInformation("Client connected using unsupported version: {clientVersion} ({version})", clientVersion.Value, clientVersion.ToString());

                using var packet = MessageWriter.Get(MessageType.Reliable);

                var message = versionCompare switch
                {
                    ICompatibilityManager.VersionCompareResult.ClientTooOld => DisconnectMessages.VersionClientTooOld,
                    ICompatibilityManager.VersionCompareResult.ServerTooOld => DisconnectMessages.VersionServerTooOld,
                    ICompatibilityManager.VersionCompareResult.Unknown => DisconnectMessages.VersionUnsupported,
                    _ => throw new ArgumentOutOfRangeException(),
                };

                await connection.CustomDisconnectAsync(DisconnectReason.Custom, message);
                return;
            }

            // Warn when players connect using the +25 flag that disables server authority.
            // This changes game behaviour, so we'd like to know if it's in use.
            if (clientVersion.HasDisableServerAuthorityFlag)
            {
                if (!_compatibilityConfig.AllowHostAuthority)
                {
                    _logger.LogInformation("Player {Name} kicked because they requested host authority.", name);
                    await connection.CustomDisconnectAsync(DisconnectReason.Custom, DisconnectMessages.HostAuthorityUnsupported);
                    return;
                }

                _logger.LogInformation("Player {Name} connected with server authority disabled, please mention that this mode is in use when asking for support.", name);
            }

            if (name.Length > 10)
            {
                await connection.CustomDisconnectAsync(DisconnectReason.Custom, DisconnectMessages.UsernameLength);
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                await connection.CustomDisconnectAsync(DisconnectReason.Custom, DisconnectMessages.UsernameIllegalCharacters);
                return;
            }

            var client = _clientFactory.Create(connection, name, clientVersion, language, chatMode, platformSpecificData);
            var id = NextId();

            var clientIp = connection.EndPoint.Address.IsIPv4MappedToIPv6
                ? connection.EndPoint.Address.MapToIPv4().ToString()
                : connection.EndPoint.Address.ToString();

            // Look up identity from /api/user token exchange. The delta-port identity
            // takes priority: the port was allocated for this exact PUID during the
            // HTTP token exchange, so it cannot be confused with another player behind
            // the same NAT/CDN IP.
            var identity = deltaPort > 0
                ? _identityService.LookupByPort(deltaPort) ?? _identityService.Lookup(connection.EndPoint.Address)
                : _identityService.Lookup(connection.EndPoint.Address);

            if (identity != null)
            {
                // Filter placeholder friend codes (same as friend's filter)
                var fc = identity.FriendCode;
                if (PlayerIdentityService.IsPlaceholderFriendCode(fc))
                {
                    fc = string.Empty;
                }

                client.ProductUserId = identity.Puid;
                client.FriendCode = fc;
                client.Puid = identity.Puid;
                client.Items["ProductUserId"] = identity.Puid;
                client.Items["Puid"] = identity.Puid;
                client.Items["FriendCode"] = fc;
                client.Items["Fid"] = identity.Fid;
                if (deltaPort > 0)
                {
                    client.Items["DeltaPort"] = deltaPort;
                    _portPool.ConfirmPort(deltaPort);
                }

                _logger.LogInformation(
                    "[加入] {Name} ({Ip}) PUID:{Puid} FID:{Fid} FriendCode:{FC} Port:{Port}",
                    name, clientIp, identity.Puid, identity.Fid, string.IsNullOrEmpty(fc) ? "(auto)" : fc, deltaPort);
            }
            else
            {
                _logger.LogInformation(
                    "[加入] {Name} ({Ip}) PUID:(none)",
                    name, clientIp);
            }

            client.Id = id;
            _logger.LogTrace("Client connected.");
            _clients.TryAdd(id, client);

            await _eventManager.CallAsync(new ClientConnectedEvent(connection, client));
        }

        public void Remove(IClient client)
        {
            _logger.LogTrace("Client disconnected.");
            _clients.TryRemove(client.Id, out _);

            // Return the delta port lease so the port can be reused.
            if (client.Items.TryGetValue("DeltaPort", out var portObj) && portObj is int port && port > 0)
            {
                _portPool.ReturnPort(port);
            }
        }

        public bool Validate(IClient client)
        {
            return client.Id != 0
                   && _clients.TryGetValue(client.Id, out var registeredClient)
                   && ReferenceEquals(client, registeredClient);
        }
    }
}

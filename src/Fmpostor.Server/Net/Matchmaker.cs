using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Fmpostor.Api.Config;
using Fmpostor.Api.Events.Managers;
using Fmpostor.Api.Net.Messages.C2S;
using Impostor.Hazel;
using Impostor.Hazel.Udp;
using Fmpostor.Server.Events.Client;
using Fmpostor.Server.Net.Hazel;
using Fmpostor.Server.Net.Manager;
using Fmpostor.Server.WebAdmin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.Net
{
    internal class Matchmaker : IDeltaListenerManager
    {
        private readonly IEventManager _eventManager;
        private readonly ClientManager _clientManager;
        private readonly ObjectPool<MessageReader> _readerPool;
        private readonly ILogger<HazelConnection> _connectionLogger;
        private readonly ILogger<Matchmaker> _logger;
        private readonly DeltaPortPoolService _portPool;
        private readonly PlayerIdentityService _identityService;
        private readonly IOptions<AntiCheatConfig> _antiCheatOptions;
        private UdpConnectionListener? _connection;
        private readonly ConcurrentDictionary<int, UdpConnectionListener> _deltaListeners = new();
        private IPEndPoint? _mainEndPoint;

        public Matchmaker(
            IEventManager eventManager,
            ClientManager clientManager,
            ObjectPool<MessageReader> readerPool,
            ILogger<HazelConnection> connectionLogger,
            ILogger<Matchmaker> logger,
            DeltaPortPoolService portPool,
            PlayerIdentityService identityService,
            IOptions<AntiCheatConfig> antiCheatOptions)
        {
            _eventManager = eventManager;
            _clientManager = clientManager;
            _readerPool = readerPool;
            _connectionLogger = connectionLogger;
            _logger = logger;
            _portPool = portPool;
            _identityService = identityService;
            _antiCheatOptions = antiCheatOptions;

            _portPool.OnPortReturned += OnPortReturned;
        }

        public async ValueTask StartAsync(IPEndPoint ipEndPoint)
        {
            _mainEndPoint = ipEndPoint;

            var mode = ipEndPoint.AddressFamily switch
            {
                AddressFamily.InterNetwork => IPMode.IPv4,
                AddressFamily.InterNetworkV6 => IPMode.IPv6,
                _ => throw new InvalidOperationException(),
            };

            _connection = new UdpConnectionListener(ipEndPoint, _readerPool, mode)
            {
                NewConnection = e => OnNewConnection(e, 0),
            };

            await _connection.StartAsync();
        }

        /// <summary>
        ///     Starts a UDP listener on a dynamically allocated delta port.
        /// </summary>
        public async ValueTask StartDeltaListenerAsync(int port)
        {
            if (_mainEndPoint == null)
            {
                _logger.LogError("Matchmaker cannot start delta listener: main endpoint not initialized");
                return;
            }

            if (_deltaListeners.ContainsKey(port))
            {
                _logger.LogDebug("Matchmaker delta listener for port {Port} already running", port);
                return;
            }

            var ep = new IPEndPoint(_mainEndPoint.Address, port);
            var mode = _mainEndPoint.AddressFamily switch
            {
                AddressFamily.InterNetwork => IPMode.IPv4,
                AddressFamily.InterNetworkV6 => IPMode.IPv6,
                _ => IPMode.IPv4,
            };

            try
            {
                var listener = new UdpConnectionListener(ep, _readerPool, mode)
                {
                    NewConnection = e => OnNewConnection(e, port),
                };

                await listener.StartAsync();
                _deltaListeners[port] = listener;
                _logger.LogInformation("Matchmaker delta UDP listener started on port {Port}", port);
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Matchmaker failed to start delta listener on port {Port} (may be in use)", port);
                // Return the port — it's unusable.
                _portPool.ReturnPort(port);
            }
        }

        public async ValueTask StopDeltaListenerAsync(int port)
        {
            if (_deltaListeners.TryRemove(port, out var listener))
            {
                try
                {
                    await listener.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Matchmaker error disposing delta listener on port {Port}", port);
                }

                _logger.LogInformation("Matchmaker delta UDP listener stopped on port {Port}", port);
            }
        }

        public async ValueTask StopAsync()
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
            }

            foreach (var (port, listener) in _deltaListeners)
            {
                try
                {
                    await listener.DisposeAsync();
                    _logger.LogDebug("Matchmaker stopped delta listener on port {Port}", port);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Matchmaker error stopping delta listener on port {Port}", port);
                }
            }

            _deltaListeners.Clear();
        }

        private async ValueTask OnNewConnection(NewConnectionEventArgs e, int port)
        {
            // Handshake.
            HandshakeC2S.Deserialize(e.HandshakeData, out var clientVersion, out var name, out var language, out var chatMode, out var platformSpecificData);

            var connection = new HazelConnection(e.Connection, _connectionLogger, _antiCheatOptions);

            await _eventManager.CallAsync(new ClientConnectionEvent(connection, e.HandshakeData));

            // Register client
            await _clientManager.RegisterConnectionAsync(connection, name, clientVersion, language, chatMode, platformSpecificData, deltaPort: port);
        }

        private void OnPortReturned(int port)
        {
            _identityService.RemoveByPort(port);
            _ = StopDeltaListenerAsync(port);
        }
    }
}

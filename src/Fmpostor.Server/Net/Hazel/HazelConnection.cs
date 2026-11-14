using System;
using System.Net;
using System.Threading.Tasks;
using Fmpostor.Api;
using Fmpostor.Api.Config;
using Fmpostor.Api.Net;
using Impostor.Hazel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fmpostor.Server.Net.Hazel
{
    internal class HazelConnection : IHazelConnection
    {
        private readonly ILogger<HazelConnection> _logger;
        private readonly AntiCheatConfig _antiCheatConfig;

        public HazelConnection(Connection innerConnection, ILogger<HazelConnection> logger, IOptions<AntiCheatConfig> antiCheatOptions)
        {
            _logger = logger;
            _antiCheatConfig = antiCheatOptions.Value;
            InnerConnection = innerConnection;
            innerConnection.DataReceived = ConnectionOnDataReceived;
            innerConnection.Disconnected = ConnectionOnDisconnected;
        }

        public Connection InnerConnection { get; }

        public IPEndPoint EndPoint => InnerConnection.EndPoint;

        public bool IsConnected => InnerConnection.State == ConnectionState.Connected;

        public IClient? Client { get; set; }

        public float AveragePing => InnerConnection is NetworkConnection networkConnection ? networkConnection.AveragePingMs : 0;

        /// <summary>
        ///     The value Hazel's own keep-alive round trip measured, ready for display.
        /// </summary>
        /// <remarks>
        ///     直接返回 Hazel 的值, 不做任何包装。
        ///
        ///     ⚠️ 这个值有 50ms 的下限: Hazel 平滑时用的是 Math.Max(50, ...)(已用 IL 确认),
        ///     所以真实只有几毫秒的连接这里也是 50。面板优先使用 HazelRttProbe 自己测的
        ///     真实往返时间, 这里只是最后的兜底。
        ///
        ///     0 表示"还没有可用样本"(未连接, 或仍是初始值), 由调用方显示为 "-"。
        /// </remarks>
        public int DisplayPingMs
        {
            get
            {
                if (!IsConnected)
                {
                    return 0;
                }

                var ping = AveragePing;

                // 仍是初始常量说明一次往返都还没测到, 显示 "500" 是假的。
                if (ping <= 0f || Math.Abs(ping - HazelPingInitialMs) < 0.01f)
                {
                    return 0;
                }

                return (int)Math.Round(ping);
            }
        }

        /// <summary>Hazel's initial value before any round trip has been measured.</summary>
        private const float HazelPingInitialMs = 500f;


        public ValueTask SendAsync(IMessageWriter writer)
        {
            return InnerConnection.SendAsync(writer);
        }

        public ValueTask DisconnectAsync(string? reason, IMessageWriter? writer = null)
        {
            return InnerConnection.Disconnect(reason, writer as MessageWriter);
        }

        public void DisposeInnerConnection()
        {
            InnerConnection.Dispose();
        }

        private async ValueTask ConnectionOnDisconnected(DisconnectedEventArgs e)
        {
            if (Client != null)
            {
                await Client.HandleDisconnectAsync(e.Reason);
            }
        }

        private async ValueTask ConnectionOnDataReceived(DataReceivedEventArgs e)
        {
            if (Client == null)
            {
                return;
            }

            // Check raw message size against the configured limit.
            // Innersloth requires full packet ≤ 1200 bytes (1168 bytes payload after 32 bytes IP+UDP headers).
            if (e.Message.Length > _antiCheatConfig.PacketSizeLimit)
            {
                if (await Client.ReportCheatAsync(
                        new CheatContext("RootMessage"),
                        CheatCategory.PacketSize,
                        $"Received a message that is too large, length: {e.Message.Length}"))
                {
                    return;
                }
            }

            while (true)
            {
                if (e.Message.Position >= e.Message.Length)
                {
                    break;
                }

                using (var message = e.Message.ReadMessage())
                {
                    await Client.HandleMessageAsync(message, e.Type);
                }

                if (!IsConnected)
                {
                    break;
                }
            }
        }
    }
}

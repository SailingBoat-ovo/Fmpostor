using System;
using Fmpostor.Api.Innersloth;

namespace Fmpostor.Api.Net.Messages.S2C
{
    public class Message01JoinGameS2C
    {
        public static void SerializeJoin(IMessageWriter writer, bool clear, int gameCode, IClientPlayer player, int hostId)
        {
            if (clear)
            {
                writer.Clear(MessageType.Reliable);
            }

            writer.StartMessage(MessageFlags.JoinGame);
            writer.Write(gameCode);
            writer.Write(player.Client.Id);
            writer.Write(hostId);
            writer.Write(player.Client.Name);
            player.Client.PlatformSpecificData.Serialize(writer);
            writer.WritePacked(player.Character?.PlayerInfo?.PlayerLevel ?? 1);

            // 占位代码(含取码失败时的 failauth#xxxx)不下发给客户端。
            writer.Write(player.Client.Puid ?? string.Empty);
            writer.Write(FriendCodeFilter.ForClients(player.Client.FriendCode));
            writer.EndMessage();
        }

        [Obsolete("JoinGame errors are no longer used by 2021.11.9 and up, disconnect clients instead.")]
        public static void SerializeError(IMessageWriter writer, bool clear, DisconnectReason reason, string? message = null)
        {
            if (clear)
            {
                writer.Clear(MessageType.Reliable);
            }

            writer.StartMessage(MessageFlags.JoinGame);
            writer.Write((int)reason);

            if (reason == DisconnectReason.Custom)
            {
                if (message == null)
                {
                    throw new ArgumentNullException(nameof(message));
                }

                writer.Write(message);
            }

            writer.EndMessage();
        }

        public static void Deserialize(IMessageReader reader)
        {
            throw new NotImplementedException();
        }
    }
}

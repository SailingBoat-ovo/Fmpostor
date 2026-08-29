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

            // Filter placeholder friend codes (same as friend's code)
            var friendCode = player.Client.FriendCode;
            if (friendCode == "kidcode#8888" || friendCode == "nocode#9999" || friendCode == "unchecked#0001")
            {
                friendCode = string.Empty;
            }

            writer.Write(player.Client.Puid);
            writer.Write(friendCode);
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

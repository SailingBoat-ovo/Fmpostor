using System;

namespace Fmpostor.Api.Net.Messages.S2C
{
    public static class Message07JoinedGameS2C
    {
        public static void Serialize(IMessageWriter writer, bool clear, int gameCode, int playerId, int hostId, IClientPlayer[] otherPlayers, bool writepuid = true)
        {
            if (clear)
            {
                writer.Clear(MessageType.Reliable);
            }

            writer.StartMessage(MessageFlags.JoinedGame);
            writer.Write(gameCode);
            writer.Write(playerId);
            writer.Write(hostId);
            writer.WritePacked(otherPlayers.Length);

            foreach (var ply in otherPlayers)
            {
                writer.WritePacked(ply.Client.Id);
                writer.Write(ply.Client.Name);
                ply.Client.PlatformSpecificData.Serialize(writer);
                writer.WritePacked(ply.Character?.PlayerInfo?.PlayerLevel ?? 1);

                if (writepuid)
                {
                    // Filter placeholder friend codes (same as friend's code)
                    var friendCode = ply.Client.FriendCode;
                    if (friendCode == "kidcode#8888" || friendCode == "nocode#9999" || friendCode == "unchecked#0001")
                    {
                        friendCode = string.Empty;
                    }

                    writer.Write(ply.Client.Puid);
                    writer.Write(friendCode);
                }
            }

            writer.EndMessage();
        }

        public static void Deserialize(IMessageReader reader)
        {
            throw new NotImplementedException();
        }
    }
}

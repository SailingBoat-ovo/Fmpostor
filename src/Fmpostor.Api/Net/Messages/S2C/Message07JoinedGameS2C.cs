using System;
using Fmpostor.Api.Innersloth;

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
                    // 占位代码(含取码失败时的 failauth#xxxx)不下发给客户端。
                    writer.Write(ply.Client.Puid ?? string.Empty);
                    writer.Write(FriendCodeFilter.ForClients(ply.Client.FriendCode));
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

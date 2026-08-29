using System;
using Fmpostor.Api.Games;

namespace Fmpostor.Api.Net.Messages.C2S
{
    public static class Message01JoinGameC2S
    {
        public static void Deserialize(IMessageReader reader, out GameCode gameCode, out string productUserId, out string friendCode)
        {
            gameCode = reader.ReadInt32();
            reader.ReadBoolean(); // no crossplay
            // Read FriendCode and ProductUserId if the client sent them (newer Among Us versions)
            productUserId = reader.Position < reader.Length ? reader.ReadString() : string.Empty;
            friendCode = reader.Position < reader.Length ? reader.ReadString() : string.Empty;
        }

        public static void Deserialize(IMessageReader reader, out GameCode gameCode)
        {
            gameCode = reader.ReadInt32();
            reader.ReadBoolean(); // no crossplay
        }

        public static void Serialize(IMessageWriter writer)
        {
            throw new NotImplementedException();
        }
    }
}

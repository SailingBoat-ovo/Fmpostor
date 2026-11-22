using System;
using Fmpostor.Api.Innersloth;

namespace Fmpostor.Api.Net.Messages.C2S
{
    /// <summary>
    ///     A player used the game's report menu on another player in the room.
    /// </summary>
    /// <remarks>
    ///     报文: [房间码 int32][被举报者 clientId packed][原因 byte]。
    ///     房间码由调用方先读并校验(与踢人等房间内报文一致), 这里只读后两项。
    /// </remarks>
    public static class Message17ReportPlayerC2S
    {
        public static void Serialize(IMessageWriter writer)
        {
            throw new NotImplementedException();
        }

        public static void Deserialize(IMessageReader reader, out int targetClientId, out ReportReasons reason)
        {
            targetClientId = reader.ReadPackedInt32();
            reason = (ReportReasons)reader.ReadByte();
        }
    }
}

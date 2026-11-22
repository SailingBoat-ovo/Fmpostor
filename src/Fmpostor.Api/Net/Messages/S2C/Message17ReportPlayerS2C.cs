using System;
using Fmpostor.Api.Innersloth;

namespace Fmpostor.Api.Net.Messages.S2C
{
    /// <summary>
    ///     Answer to a report: the game shows "report sent" (or the reason it was not sent).
    /// </summary>
    /// <remarks>
    ///     报文: [被举报者 clientId packed][原因 int32][结果 byte][被举报者名字 string]。
    ///     原因在应答里是 4 字节整数(与请求里的 1 字节不同), 客户端就是这样读的。
    /// </remarks>
    public static class Message17ReportPlayerS2C
    {
        public static void Serialize(IMessageWriter writer, bool clear, int targetClientId, ReportReasons reason, ReportOutcome outcome, string playerName)
        {
            if (clear)
            {
                writer.Clear(MessageType.Reliable);
            }

            writer.StartMessage(MessageFlags.ReportPlayer);
            writer.WritePacked(targetClientId);
            writer.Write((int)reason);
            writer.Write((byte)outcome);
            writer.Write(playerName ?? string.Empty);
            writer.EndMessage();
        }

        public static void Deserialize(IMessageReader reader)
        {
            throw new NotImplementedException();
        }
    }
}

using System;

namespace Fmpostor.Api.Innersloth
{
    /// <summary>
    ///     Decides which friend code a game client is allowed to see for a player.
    /// </summary>
    /// <remarks>
    ///     发给客户端的好友代码只能是真实的代码。
    ///     占位值(未成年账号、没有代码、尚未校验、取码失败时服务端生成的 failauth#xxxx)
    ///     一律换成空字符串: 客户端看到空值会不显示代码, 而看到假代码会让玩家拿着它去加好友并失败。
    ///     面板与统计仍然使用原值, 这里只影响下发给游戏客户端的内容。
    /// </remarks>
    public static class FriendCodeFilter
    {
        /// <summary>Whether <paramref name="friendCode" /> is a placeholder rather than a real code.</summary>
        public static bool IsPlaceholder(string? friendCode)
        {
            if (string.IsNullOrWhiteSpace(friendCode))
            {
                return true;
            }

            return string.Equals(friendCode, "kidcode#8888", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendCode, "nocode#9999", StringComparison.OrdinalIgnoreCase)
                || string.Equals(friendCode, "unchecked#0001", StringComparison.OrdinalIgnoreCase)
                || friendCode.StartsWith("failauth#", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The value to send to game clients: the real code, or an empty string.</summary>
        public static string ForClients(string? friendCode)
        {
            return IsPlaceholder(friendCode) ? string.Empty : friendCode!.Trim();
        }
    }
}

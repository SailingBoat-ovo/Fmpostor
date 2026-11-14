using System.Collections.Concurrent;
using System.Collections.Generic;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Net.Inner.Objects
{
    internal partial class InnerGameData : IInnerGameData
    {
        private readonly ConcurrentDictionary<byte, InnerPlayerInfo> _allPlayers = new();
        private readonly ConcurrentDictionary<int, InnerPlayerInfo> _allPlayersByClientId = new();

        public int PlayerCount => _allPlayers.Count;

        public IReadOnlyDictionary<byte, InnerPlayerInfo> Players => _allPlayers;

        internal IReadOnlyDictionary<int, InnerPlayerInfo> PlayersByClientId => _allPlayersByClientId;

        public InnerPlayerInfo? GetPlayerById(byte id)
        {
            if (id == byte.MaxValue)
            {
                return null;
            }

            return _allPlayers.TryGetValue(id, out var player) ? player : null;
        }

        internal bool AddPlayer(InnerPlayerInfo playerInfo)
        {
            return _allPlayers.TryAdd(playerInfo.PlayerId, playerInfo) &&
                _allPlayersByClientId.TryAdd(playerInfo.ClientId, playerInfo);
        }

        internal void RemovePlayer(byte playerId)
        {
            _allPlayers.TryRemove(playerId, out var player);

            if (player != null)
            {
                _allPlayersByClientId.TryRemove(player.ClientId, out _);
            }
        }

        /// <summary>
        ///     Moves a player's client-id index entry onto a new client id.
        ///     Setting <see cref="InnerPlayerInfo.ClientId" /> alone is not enough:
        ///     <see cref="_allPlayersByClientId" /> is the dictionary that
        ///     lookups (and therefore the late-join / resume sync) actually use,
        ///     so it has to be re-keyed as well.
        /// </summary>
        /// <returns>True when the index was moved.</returns>
        internal bool ReKeyClientId(byte playerId, int oldClientId, int newClientId)
        {
            if (oldClientId == newClientId)
            {
                return true;
            }

            if (!_allPlayers.TryGetValue(playerId, out var playerInfo))
            {
                return false;
            }

            // The property is the source of truth for everything that reads it;
            // the dictionary is the source of truth for lookups. Keep both
            // consistent, and never leave the old key resolving to this player.
            var oldKeyWas = playerInfo.ClientId;
            playerInfo.ClientId = newClientId;

            _allPlayersByClientId.TryRemove(oldClientId, out _);
            if (oldKeyWas != newClientId)
            {
                _allPlayersByClientId.TryRemove(oldKeyWas, out _);
            }

            return _allPlayersByClientId.TryAdd(newClientId, playerInfo);
        }

        internal byte GetNextAvailablePlayerId()
        {
            for (byte i = 0; i < byte.MaxValue; i++)
            {
                if (!Players.ContainsKey(i))
                {
                    return i;
                }
            }

            return byte.MaxValue;
        }
    }
}

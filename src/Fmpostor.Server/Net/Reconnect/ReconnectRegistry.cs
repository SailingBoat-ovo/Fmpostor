using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Fmpostor.Api.Config;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Net.Reconnect
{
    /// <summary>
    ///     Everything the server remembers about a player that may reconnect.
    /// </summary>
    internal sealed class ReconnectSnapshot
    {
        public string IdentityKey { get; init; } = string.Empty;

        public string Puid { get; init; } = string.Empty;

        public string FriendCode { get; init; } = string.Empty;

        public string IpAddress { get; init; } = string.Empty;

        /// <summary>ClientId the player had while connected.</summary>
        public int OldClientId { get; init; }

        /// <summary>The in-game slot (GamePlayerId) that must be preserved.</summary>
        public byte PlayerId { get; init; }

        public string PlayerName { get; init; } = string.Empty;

        public DateTimeOffset DisconnectedAt { get; init; }

        /// <summary>Deadline after which this slot is released.</summary>
        public DateTimeOffset ExpiresAt { get; init; }

        public bool IsHost { get; init; }

        /// <summary>
        ///     Opaque mod-state blob as reported by a connected client.
        ///     The server never interprets it.
        /// </summary>
        public byte[]? ModState { get; set; }

        /// <summary>Monotonic sequence supplied by the reporting client.</summary>
        public long ModStateSeq { get; set; }

        public DateTimeOffset? ModStateAt { get; set; }

        /// <summary>True when at least one client could report the mod state.</summary>
        public bool ModStateComplete { get; set; }

        /// <summary>How the player left, for diagnostics and policy.</summary>

        /// <summary>
        ///     NetId of the preserved InnerPlayerControl, so a resume can
        ///     re-spawn the original body without searching for it.
        /// </summary>
        public uint CharacterNetId { get; set; }
        public string DisconnectReason { get; init; } = string.Empty;
    }

    /// <summary>
    ///     A slot reserved for a player that is expected to come back.
    /// </summary>
    internal sealed class PendingSlot
    {
        private const int StateFree = 0;
        private const int StateClaimed = 1;
        private const int StateConsumed = 2;

        private int _state;

        public ReconnectSnapshot Snapshot { get; init; } = new();

        /// <summary>True once a resume has been completed for this slot.</summary>
        public bool Consumed => Volatile.Read(ref _state) == StateConsumed;

        /// <summary>
        ///     True when a resume is in flight (claimed but not finished). The grace
        ///     timer must not release such a slot - the join is still being processed.
        /// </summary>
        public bool Claimed => Volatile.Read(ref _state) != StateFree;

        /// <summary>
        ///     Atomically claims the slot so exactly one concurrent join can resume it
        ///     (two connections carrying the same identity would otherwise both match).
        ///     Pair with <see cref="ReleaseClaim" /> when the attempt aborts.
        /// </summary>
        public bool TryClaim()
        {
            return Interlocked.CompareExchange(ref _state, StateClaimed, StateFree) == StateFree;
        }

        /// <summary>
        ///     Gives a claimed-but-unused slot back so a later attempt can still resume
        ///     it (the attempt aborted before it could use the slot).
        /// </summary>
        public void ReleaseClaim()
        {
            Interlocked.CompareExchange(ref _state, StateFree, StateClaimed);
        }

        /// <summary>Finishes the resume: the slot is one-shot and never reusable.</summary>
        public void MarkConsumed()
        {
            Interlocked.Exchange(ref _state, StateConsumed);
        }

        /// <summary>New ClientId after a successful resume, for diagnostics.</summary>
        public int? ResumedClientId { get; set; }
    }

    /// <summary>
    ///     Server-side registry of reconnectable players, keyed by identity.
    ///     One registry per game.
    /// </summary>
    internal sealed class ReconnectRegistry
    {
        private static readonly byte[] KeySalt = Encoding.UTF8.GetBytes("Fmpostor.ReconnectTicket.v1");

        private readonly ReconnectConfig _config;
        private readonly ILogger _logger;
        private readonly byte[] _ticketKey;

        private readonly ConcurrentDictionary<string, PendingSlot> _byIdentity =
            new ConcurrentDictionary<string, PendingSlot>(StringComparer.Ordinal);

        // playerId -> identityKey, so a disconnect can find the right snapshot.
        private readonly ConcurrentDictionary<byte, string> _identityByPlayerId =
            new ConcurrentDictionary<byte, string>();

        /// <summary>
        ///     Live mod-state cache: playerId -> latest blob reported by any
        ///     connected client. Kept up to date for every player while they are
        ///     connected, so a disconnect can snapshot it instantly instead of
        ///     running a request/response round trip during a network event.
        /// </summary>
        private readonly ConcurrentDictionary<byte, LiveModState> _liveModState =
            new ConcurrentDictionary<byte, LiveModState>();

        private int _slotCount;

        private sealed class LiveModState
        {
            public byte[] Blob { get; set; } = Array.Empty<byte>();

            public long Seq { get; set; }

            public DateTimeOffset At { get; set; }
        }

        public ReconnectRegistry(ReconnectConfig config, ILogger logger)
        {
            _config = config;
            _logger = logger;

            // Per-process random key: a ticket is only meaningful for the
            // lifetime of this server process. After a restart the in-memory
            // snapshots are gone anyway.
            _ticketKey = RandomNumberGenerator.GetBytes(32);
        }

        public bool Enabled => _config.Enabled;

        public int SlotCount => Volatile.Read(ref _slotCount);

        public IReadOnlyCollection<PendingSlot> Slots => _byIdentity.Values.ToArray();

        /// <summary>
        ///     Builds the identity key used to recognise a returning player.
        ///     Priority: PUID (stable, platform issued), then friend code, then
        ///     player name only as a last resort.
        /// </summary>
        public static string BuildIdentityKey(string? puid, string? friendCode, string? playerName)
        {
            var name = (playerName ?? string.Empty).Trim().ToLowerInvariant();

            // ── ⚠️ 名字必须参与身份键, 不能只用 PUID ──
            //
            // 这不是可选的加固, 而是必须的修正。原因在服务端自己的身份模型里:
            //   PlayerIdentityService 是【按 IP】建立映射的
            //       Register(string ipAddress, string puid, string friendCode, ...)
            //       IdentityEntry? Lookup(IPAddress? clientAddr)
            //   所以当多个客户端从同一个 IP 连入时(同机双开、同一出口 NAT),
            //   后注册者会覆盖前者的身份 —— 结果是【多个玩家拿到同一个 PUID 与好友码】。
            //
            // 实测证据(同机双开):
            //   join 帆船        puid=00023651d6af4c... fc=failauth#9452
            //   join Drayliable  puid=00023651d6af4c... fc=failauth#9452   ← 完全相同
            //
            // 如果身份键只用 PUID, 这两人的键就相同, 于是:
            //   一个人掉线留下保槽 → 另一个【仍在线的】玩家一 join 就被判定成"重连",
            //   被塞进别人的槽位, 随即失败并被踢 —— 表现为"要等 5 分钟才能再进游戏"
            //   (5 分钟正是配置里的 GracePeriodSeconds=300, 槽位把位置占满了)。
            //
            // 加上名字后, 同一 IP 下的不同玩家才能被区分。名字在对局内稳定,
            // 因此对正常的"掉线再回来"没有影响。
            if (!string.IsNullOrWhiteSpace(puid))
            {
                return "puid:" + puid!.Trim().ToLowerInvariant() + "|" + name;
            }

            if (!string.IsNullOrWhiteSpace(friendCode))
            {
                return "fc:" + friendCode!.Trim().ToLowerInvariant() + "|" + name;
            }

            return "name:" + (playerName ?? string.Empty).Trim().ToLowerInvariant();
        }

        /// <summary>
        ///     Reserves a slot. Called from PlayerRemove when a player leaves.
        /// </summary>
        public bool TryReserve(
            string identityKey,
            byte playerId,
            int oldClientId,
            string puid,
            string friendCode,
            string ipAddress,
            string playerName,
            bool isHost,
            string disconnectReason,
            DateTimeOffset now,
            out PendingSlot? slot)
        {
            slot = null;

            if (!_config.Enabled || string.IsNullOrEmpty(identityKey))
            {
                return false;
            }

            if (!_config.AllowVanillaClients &&
                string.IsNullOrEmpty(puid) &&
                string.IsNullOrEmpty(friendCode))
            {
                return false;
            }

            if (Volatile.Read(ref _slotCount) >= _config.MaxPendingSlotsPerGame)
            {
                _logger.LogWarning(
                    "Reconnect: slot limit reached ({Limit}), not reserving for {Identity}.",
                    _config.MaxPendingSlotsPerGame,
                    identityKey);
                return false;
            }

            var snapshot = new ReconnectSnapshot
            {
                IdentityKey = identityKey,
                Puid = puid,
                FriendCode = friendCode,
                IpAddress = ipAddress,
                OldClientId = oldClientId,
                PlayerId = playerId,
                PlayerName = playerName,
                DisconnectedAt = now,
                ExpiresAt = now.AddSeconds(_config.GracePeriodSeconds),
                IsHost = isHost,
                DisconnectReason = disconnectReason,
            };

            var created = new PendingSlot { Snapshot = snapshot };

            // Take the freshest mod-state blob we already hold for this player, but only
            // while it is still within SnapshotTtlSeconds: a blob older than that means
            // the reporting client stopped updating long before the disconnect, so
            // restoring it would put the player back into an outdated mod state.
            // (Without this check the knob had no effect at all.)
            if (_config.CaptureModState && _liveModState.TryGetValue(playerId, out var live))
            {
                var age = now - live.At;
                var ttl = TimeSpan.FromSeconds(Math.Max(1, _config.SnapshotTtlSeconds));

                if (age <= ttl)
                {
                    snapshot.ModState = live.Blob;
                    snapshot.ModStateSeq = live.Seq;
                    snapshot.ModStateAt = live.At;
                    snapshot.ModStateComplete = true;
                }
                else
                {
                    _logger.LogInformation(
                        "Reconnect: mod state for playerId {PlayerId} is {Age:F0}s old (ttl {Ttl}s); it will not be snapshotted.",
                        playerId,
                        age.TotalSeconds,
                        ttl.TotalSeconds);
                }
            }

            // Replace any stale slot for the same identity; the newer drop wins.
            if (_byIdentity.TryRemove(identityKey, out var previous))
            {
                Interlocked.Decrement(ref _slotCount);
                _identityByPlayerId.TryRemove(previous.Snapshot.PlayerId, out _);
            }

            if (!_byIdentity.TryAdd(identityKey, created))
            {
                return false;
            }

            Interlocked.Increment(ref _slotCount);
            _identityByPlayerId[playerId] = identityKey;

            _logger.LogInformation(
                "Reconnect: reserved slot for {Name} (playerId {PlayerId}, clientId {ClientId}, identity {Identity}) until {Expires:HH:mm:ss}.",
                playerName,
                playerId,
                oldClientId,
                identityKey,
                snapshot.ExpiresAt);

            slot = created;
            return true;
        }

        /// <summary>
        ///     Looks up a live (unexpired, unconsumed) slot for the identity.
        /// </summary>
        public bool TryTake(
            string identityKey,
            string ipAddress,
            DateTimeOffset now,
            out PendingSlot? slot)
        {
            slot = null;

            if (!_config.Enabled || string.IsNullOrEmpty(identityKey))
            {
                return false;
            }

            if (!_byIdentity.TryGetValue(identityKey, out var candidate))
            {
                return false;
            }

            if (candidate.Consumed || candidate.Snapshot.ExpiresAt <= now)
            {
                return false;
            }

            // A name-only identity is weak: any other player using the same
            // nickname would match it. Such a key therefore always requires the
            // same source IP, whatever the global setting says.
            var isWeakIdentity = identityKey.StartsWith("name:", StringComparison.Ordinal);

            if ((_config.RequireSameIp || isWeakIdentity) &&
                !string.Equals(candidate.Snapshot.IpAddress, ipAddress, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Reconnect: identity {Identity} tried to resume from a different IP ({Ip} != {Expected}); rejected (weakIdentity={Weak}).",
                    identityKey,
                    ipAddress,
                    candidate.Snapshot.IpAddress,
                    isWeakIdentity);
                return false;
            }

            // One-shot guarantee: claim the slot here so two concurrent joins carrying
            // the same identity cannot both resume it. The caller releases the claim if
            // the join aborts before the slot is actually used (PendingSlot.ReleaseClaim).
            if (!candidate.TryClaim())
            {
                _logger.LogInformation(
                    "Reconnect: identity {Identity} already has a resume in flight; ignored.",
                    identityKey);
                return false;
            }

            slot = candidate;
            return true;
        }

        /// <summary>
        ///     Marks a slot consumed (one-shot) after a successful resume.
        /// </summary>
        public void MarkResumed(PendingSlot slot, int newClientId)
        {
            slot.MarkConsumed();
            slot.ResumedClientId = newClientId;

            _byIdentity.TryRemove(slot.Snapshot.IdentityKey, out _);
            _identityByPlayerId.TryRemove(slot.Snapshot.PlayerId, out _);
            Interlocked.Decrement(ref _slotCount);

            _logger.LogInformation(
                "Reconnect: {Name} resumed into clientId {NewClientId} (was {OldClientId}).",
                slot.Snapshot.PlayerName,
                newClientId,
                slot.Snapshot.OldClientId);
        }


        /// <summary>
        ///     Stores the latest mod-state blob for a player. Called while the
        ///     player is still connected, so the cache is already fresh by the
        ///     time a disconnect needs it.
        /// </summary>
        public bool TryStoreLiveModState(
            byte playerId,
            byte[] blob,
            long seq,
            DateTimeOffset now,
            out string reason)
        {
            reason = string.Empty;

            if (!_config.Enabled || !_config.CaptureModState)
            {
                reason = "disabled";
                return false;
            }

            if (blob.Length == 0 || blob.Length > _config.MaxModStateBytes)
            {
                reason = "size";
                return false;
            }

            if (_liveModState.TryGetValue(playerId, out var existing))
            {
                // Only accept genuinely newer reports.
                if (existing.Seq > seq)
                {
                    reason = "stale";
                    return false;
                }

                if ((now - existing.At).TotalMilliseconds < _config.ModStateReportMinIntervalMs &&
                    existing.Seq == seq)
                {
                    reason = "rate";
                    return false;
                }

                existing.Blob = blob;
                existing.Seq = seq;
                existing.At = now;
                return true;
            }

            return _liveModState.TryAdd(
                playerId,
                new LiveModState { Blob = blob, Seq = seq, At = now });
        }

        /// <summary>
        ///     Drops the live mod-state entry for a player that has left for good.
        /// </summary>
        public void ForgetLiveModState(byte playerId)
        {
            _liveModState.TryRemove(playerId, out _);
        }

        /// <summary>
        ///     Wire information learned from the mod's own packets, needed to send a
        ///     restore frame back in exactly the shape the client's dispatcher expects.
        /// </summary>
        internal sealed class ModWireInfo
        {
            /// <summary>
            ///     The process hash the client build uses for the mod-state process.
            ///     It is computed by a mod-internal extension method the server cannot
            ///     replicate, so it is learned by observing a real report instead of
            ///     being recomputed. Reusing the observed value also means the server
            ///     stays independent of the mod version.
            /// </summary>
            public int ProcessHash { get; init; }

            /// <summary>NetId the mod addressed its own RPC to (a PlayerControl).</summary>
            public uint TargetNetId { get; init; }

            public int ReporterClientId { get; init; }

            public DateTimeOffset At { get; init; }
        }

        private volatile ModWireInfo? _wireInfo;

        /// <summary>
        ///     Per-reporter wire info. The mod's process hash is a property of the mod
        ///     build, so a room that mixes two mod versions must not reuse the hash of
        ///     whichever client happened to report last: the reconnecting player's own
        ///     earlier reports give their exact hash.
        /// </summary>
        private readonly ConcurrentDictionary<int, ModWireInfo> _wireInfoByReporter = new();

        /// <summary>
        ///     Remembers the hash/netId pair seen in a structurally valid mod report.
        /// </summary>
        public void RememberWireInfo(int processHash, uint targetNetId, int reporterClientId, DateTimeOffset now)
        {
            var info = new ModWireInfo
            {
                ProcessHash = processHash,
                TargetNetId = targetNetId,
                ReporterClientId = reporterClientId,
                At = now,
            };

            _wireInfo = info;

            // ★ 去重必须按【上报者自己】的旧值比较, 不能和全局 _wireInfo 比。
            //   原因: _wireInfo 是全局的, 会被其它客户端的上报覆盖, 而每个客户端的
            //   netId 天生不同 —— 于是"和全局比"永远不相等, 日志变成每秒 N 条刷屏
            //   (实测 4 人局每秒 4 条, 把真正有用的重连日志全埋了)。
            _wireInfoByReporter.TryGetValue(reporterClientId, out var previousForMe);
            _wireInfoByReporter[reporterClientId] = info;

            if (previousForMe == null
                || previousForMe.ProcessHash != processHash
                || previousForMe.TargetNetId != targetNetId)
            {
                _logger.LogInformation(
                    "Reconnect: mod wire info learned from client {ClientId}: hash={Hash}, netId={NetId}.",
                    reporterClientId,
                    processHash,
                    targetNetId);
            }
        }

        /// <summary>
        ///     True when we have a hash/netId pair to address a restore frame with.
        ///     The entry recorded for <paramref name="preferClientId" /> (the player's
        ///     own connection before it dropped) wins over the last-seen global one.
        /// </summary>
        /// <remarks>
        ///     ⚠️ 这个方法的全局回退只适合"服务端自己拼包"的场景。凡是【发给某个具体客户端】
        ///     的帧, 都必须用 <see cref="TryGetWireInfoForClient" /> —— 否则会把上一条连接的
        ///     进程哈希用在普通客户端身上, 客户端认不出来, 等于凭空多发了一个垃圾包。
        /// </remarks>
        public bool TryGetWireInfo(int? preferClientId, [NotNullWhen(true)] out ModWireInfo? info)
        {
            if (preferClientId.HasValue &&
                _wireInfoByReporter.TryGetValue(preferClientId.Value, out var mine))
            {
                info = mine;
                return true;
            }

            info = _wireInfo;
            return info != null;
        }

        /// <summary>
        ///     Exact lookup: only returns wire info that this very client reported.
        /// </summary>
        /// <remarks>
        ///     发送任何"服务端 → 该客户端"的自定义帧之前都用这个。返回 false 就意味着
        ///     这条连接【从未上报过模组状态】—— 它要么是原版客户端, 要么还没跑到上报那一步,
        ///     两种情况都不该收到模组帧: 发了它也不认, 只会污染正常游玩。
        /// </remarks>
        public bool TryGetWireInfoForClient(int clientId, [NotNullWhen(true)] out ModWireInfo? info)
        {
            return _wireInfoByReporter.TryGetValue(clientId, out info);
        }

        /// <summary>
        ///     Looks up a still-pending slot by identity key.
        /// </summary>
        public bool TryGetPendingByIdentity(string identityKey, out PendingSlot? slot)
        {
            slot = null;

            if (string.IsNullOrEmpty(identityKey))
            {
                return false;
            }

            if (!_byIdentity.TryGetValue(identityKey, out var candidate))
            {
                return false;
            }

            if (candidate.Claimed)
            {
                // Either consumed (resume done) or a resume is in flight right now.
                return false;
            }

            slot = candidate;
            return true;
        }

        /// <summary>
        ///     Puts a slot that was taken by a resume back into the registry, because
        ///     the resumed connection dropped before the resume was confirmed.
        /// </summary>
        /// <remarks>
        ///     The same slot object is reused, so the captured mod state and the original
        ///     deadline stay as they were: a failed attempt neither loses the state nor
        ///     extends the grace period. A newer reservation for the same identity wins.
        /// </remarks>
        public bool Restore(PendingSlot slot, DateTimeOffset now)
        {
            if (!_config.Enabled || slot.Consumed || slot.Snapshot.ExpiresAt <= now)
            {
                return false;
            }

            var identityKey = slot.Snapshot.IdentityKey;
            if (string.IsNullOrEmpty(identityKey))
            {
                return false;
            }

            slot.ReleaseClaim();

            if (!_byIdentity.TryAdd(identityKey, slot))
            {
                return false;
            }

            Interlocked.Increment(ref _slotCount);
            _identityByPlayerId[slot.Snapshot.PlayerId] = identityKey;

            _logger.LogInformation(
                "Reconnect: handed the slot of {Name} (playerId {PlayerId}) back; it stays valid until {Expires:HH:mm:ss}.",
                slot.Snapshot.PlayerName,
                slot.Snapshot.PlayerId,
                slot.Snapshot.ExpiresAt);

            return true;
        }

        /// <summary>
        ///     True when a live, unclaimed slot exists for the identity. Does not claim it.
        /// </summary>
        public bool HasPending(string identityKey, DateTimeOffset now)
        {
            return TryGetPendingByIdentity(identityKey, out var slot)
                   && slot != null
                   && !slot.Consumed
                   && slot.Snapshot.ExpiresAt > now;
        }

        /// <summary>
        ///     Removes a pending slot by in-game player id (grace period over).
        /// </summary>
        public bool DropSlot(byte playerId)
        {
            if (!_identityByPlayerId.TryRemove(playerId, out var identityKey))
            {
                return false;
            }

            if (_byIdentity.TryRemove(identityKey, out _))
            {
                Interlocked.Decrement(ref _slotCount);
                ForgetLiveModState(playerId);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Releases expired slots. Returns the number removed.
        /// </summary>
        public int Sweep(DateTimeOffset now)
        {
            var removed = 0;

            foreach (var pair in _byIdentity.ToArray())
            {
                if (pair.Value.Consumed || pair.Value.Snapshot.ExpiresAt > now)
                {
                    continue;
                }

                if (_byIdentity.TryRemove(pair.Key, out var slot))
                {
                    _identityByPlayerId.TryRemove(slot.Snapshot.PlayerId, out _);
                    Interlocked.Decrement(ref _slotCount);
                    removed++;

                    _logger.LogInformation(
                        "Reconnect: grace period expired for {Name} (playerId {PlayerId}); slot released.",
                        slot.Snapshot.PlayerName,
                        slot.Snapshot.PlayerId);
                }
            }

            return removed;
        }

        /// <summary>
        ///     Drops every slot (game over / game destroyed).
        /// </summary>
        public void Clear()
        {
            _byIdentity.Clear();
            _identityByPlayerId.Clear();
            Volatile.Write(ref _slotCount, 0);
        }

        /// <summary>
        ///     Issues a one-shot resume ticket bound to an identity. The resume
        ///     decision itself is made from server-side identity matching; the
        ///     ticket gives a client a way to prove continuity and is a hook for
        ///     future hardening.
        /// </summary>
        public string IssueTicket(string identityKey, DateTimeOffset expiresAt)
        {
            var payload = string.Concat(
                identityKey,
                "|",
                expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            var mac = HMACSHA256.HashData(_ticketKey, Concat(KeySalt, payloadBytes));
            return Convert.ToBase64String(Concat(payloadBytes, mac));
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }
    }
}



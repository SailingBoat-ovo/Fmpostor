namespace Fmpostor.Api.Config
{
    /// <summary>
    ///     Configuration for the disconnect-reconnect feature.
    ///     Everything is disabled by default so that existing deployments are
    ///     completely unaffected until an operator opts in.
    /// </summary>
    public class ReconnectConfig
    {
        public const string Section = "Reconnect";

        /// <summary>
        ///     Master switch. When false, not a single line of the reconnect
        ///     machinery runs and the server behaves exactly as before.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        ///     How long a disconnected player keeps their slot (character,
        ///     PlayerId, role, tasks, position) reserved. Within this window the
        ///     same identity may reconnect and resume its own body.
        /// </summary>
        public int GracePeriodSeconds { get; set; } = 45;

        /// <summary>
        ///     How long a captured mod-state snapshot stays valid: a blob older than
        ///     this is not snapshotted when the player disconnects (the reporting
        ///     clients stopped updating it long ago, so restoring it would put the
        ///     player back into an outdated mod state).
        /// </summary>
        public int SnapshotTtlSeconds { get; set; } = 120;

        /// <summary>
        ///     Maximum number of simultaneously reserved slots per game.
        /// </summary>
        public int MaxPendingSlotsPerGame { get; set; } = 16;

        /// <summary>
        ///     Allow clients that never answered the mod-state handshake to
        ///     reconnect as well. They still get vanilla state restored.
        /// </summary>
        public bool AllowVanillaClients { get; set; } = true;

        /// <summary>
        ///     Require the reconnecting connection to come from the same remote
        ///     IP address that disconnected.
        /// </summary>
        public bool RequireSameIp { get; set; } = false;

        /// <summary>
        ///     Only genuine network drops reserve a slot; deliberate kicks and
        ///     bans do not.
        /// </summary>
        /// <remarks>
        ///     NOTE (server side): kicks and bans are already excluded structurally
        ///     (PlayerRemove's isBan / fromKick), which is what this knob was meant to
        ///     express. A deliberate "leave game" still reserves a slot today, because
        ///     PlayerRemove does not receive the disconnect reason. Wire it here if the
        ///     stricter reading is wanted - it is deliberately left alone so the
        ///     behaviour does not change while the feature is being tested.
        /// </remarks>
        public bool OnlyNetworkDrops { get; set; } = true;

        /// <summary>
        ///     Ask connected clients for a mod-state snapshot of a player when
        ///     that player disconnects.
        /// </summary>
        public bool CaptureModState { get; set; } = true;

        /// <summary>
        ///     Maximum accepted size of a single mod-state snapshot blob.
        /// </summary>
        public int MaxModStateBytes { get; set; } = 4096;

        /// <summary>
        ///     How long to wait, after a disconnect, for clients to answer the
        ///     mod-state request.
        /// </summary>
        /// <remarks>
        ///     OBSOLETE / unused: the "ask the remaining clients for a snapshot at
        ///     disconnect time" round trip was replaced by the always-on live cache
        ///     (clients report on a heartbeat, so the cache is already fresh when a
        ///     disconnect happens). Kept so existing configuration files keep binding.
        /// </remarks>
        public int ModStateCollectMs { get; set; } = 1500;

        /// <summary>
        ///     Minimum interval between two mod-state reports for one player.
        /// </summary>
        public int ModStateReportMinIntervalMs { get; set; } = 1000;

        /// <summary>
        ///     ⚠️ DEVELOPMENT / TESTING ONLY — keep false in production.
        ///
        ///     Normally the host never keeps a reserved slot: a resume always arrives on
        ///     a NEW ClientId while HostId is keyed by ClientId, so host ownership cannot
        ///     be handed back without real host migration.
        ///
        ///     Turning this on lets the host reserve a slot as well, which makes a
        ///     single-machine reconnect test possible (solo game where the player is the
        ///     host). After resuming, HostId still points at the OLD ClientId, so the
        ///     resumed player is treated as an ordinary player and cannot start rounds -
        ///     use it only to observe slot / role / body restore, e.g.:
        ///         "Reconnect": { "Enabled": true, "DevAllowHostReserve": true,
        ///                        "GracePeriodSeconds": 300 }
        /// </summary>
        public bool DevAllowHostReserve { get; set; } = false;

        /// <summary>
        ///     ⚠️ DEVELOPMENT / TESTING ONLY — keep false in production.
        /// </summary>
        /// <remarks>
        ///     Normal behaviour: a kicked player does NOT keep a reserved slot. That is
        ///     deliberate — a kick means "I do not want you here", so handing the slot
        ///     back would defeat the purpose. It is enforced in PlayerRemove, which
        ///     passes fromKick: true and therefore skips TryDeferReconnectSlot.
        ///
        ///     Turning this on makes a kick behave like a dropped connection for the
        ///     purposes of the grace period, which is what you want when you are
        ///     testing reconnection and kicking is the easiest way to produce a
        ///     disconnect (no need to unplug anything, and the room stays alive
        ///     because the other players are still connected).
        ///
        ///     With it on, a kicked player CAN come back within GracePeriodSeconds.
        ///     Bans are never affected by this switch - a ban always tears the slot
        ///     down for good. Example:
        ///         "Reconnect": { "Enabled": true, "DevAllowKickReserve": true,
        ///                        "GracePeriodSeconds": 300 }
        /// </remarks>
        public bool DevAllowKickReserve { get; set; } = false;

        /// <summary>
        ///     Whether a brand-new player may join a round that has already started,
        ///     as a neutral spectator. Off by default: joining a running round is
        ///     normally refused with "game has already started".
        /// </summary>
        /// <remarks>
        ///     A spectator has no side and no win condition, is always a ghost, and is
        ///     excluded from win checks. Players and spectators share the 32 player-id
        ///     budget (player ids are used as bit positions), so
        ///     <c>players + spectators &lt;= 32</c>.
        /// </remarks>
        public bool EnableSpectator { get; set; } = false;

        /// <summary>Upper bound on concurrent spectators in one game.</summary>
        public int MaxSpectators { get; set; } = 3;

        /// <summary>
        ///     Longest time after voting completes that joins into the running round are
        ///     still refused, in seconds (covers the result display, the exile animation
        ///     and the post-meeting respawn step).
        /// </summary>
        /// <remarks>
        ///     During a meeting the server turns away reconnects and spectator joins
        ///     before they touch any reserved slot. The meeting window starts when the
        ///     host spawns the meeting object and ends this many seconds after voting
        ///     completes (the meeting object itself is destroyed locally on every client,
        ///     so no despawn ever reaches the server). When the room's host mod reports
        ///     that the post-meeting scene is over, joins reopen 2 seconds after that
        ///     instead, if that is earlier. 0 disables the extra buffer.
        /// </remarks>
        public int MeetingJoinGraceSeconds { get; set; } = 17;

        /// <summary>
        ///     How long a resumed player must stay connected after their body appears
        ///     before the reserved slot is spent for good, in seconds.
        /// </summary>
        /// <remarks>
        ///     If the connection drops before that, the original slot (including the
        ///     captured mod state) is handed back, so the next attempt can still
        ///     restore the player. Kicks and bans never hand it back.
        /// </remarks>
        public int ResumeConfirmSeconds { get; set; } = 10;
    }
}

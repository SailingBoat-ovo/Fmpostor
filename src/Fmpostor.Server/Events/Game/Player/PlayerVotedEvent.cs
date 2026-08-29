using Fmpostor.Api.Events.Player;
using Fmpostor.Api.Games;
using Fmpostor.Api.Net;
using Fmpostor.Api.Net.Inner.Objects;

namespace Fmpostor.Server.Events.Player
{
    public class PlayerVotedEvent : IPlayerVotedEvent
    {
        public PlayerVotedEvent(IGame game, IClientPlayer clientPlayer, IInnerPlayerControl playerControl, VoteType voteType, IInnerPlayerControl? votedFor)
        {
            Game = game;
            ClientPlayer = clientPlayer;
            PlayerControl = playerControl;
            VoteType = voteType;
            VotedFor = votedFor;
        }

        public IGame Game { get; }

        public IClientPlayer ClientPlayer { get; }

        public IInnerPlayerControl PlayerControl { get; }

        public IInnerPlayerControl? VotedFor { get; }

        public VoteType VoteType { get; }
    }
}

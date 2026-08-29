using Fmpostor.Api;
using Fmpostor.Api.Events;
using Fmpostor.Api.Games;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Net;

namespace Fmpostor.Server.Events
{
    public class GameCreationEvent : IGameCreationEvent
    {
        private readonly IGameManager _gameManager;
        private GameCode? _gameCode;

        public GameCreationEvent(IGameManager gameManager, IClient? client)
        {
            _gameManager = gameManager;
            Client = client;
        }

        public IClient? Client { get; }

        public GameCode? GameCode
        {
            get => _gameCode;
            set
            {
                if (value.HasValue)
                {
                    if (value.Value.IsInvalid)
                    {
                        throw new FmpostorException("GameCode is invalid.");
                    }

                    if (_gameManager.Find(value.Value) != null)
                    {
                        throw new FmpostorException($"GameCode [{value.Value.Code}] is already used.");
                    }
                }

                _gameCode = value;
            }
        }

        public bool IsCancelled { get; set; }
    }
}

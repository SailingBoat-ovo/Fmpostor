using System.Threading.Tasks;
using Fmpostor.Api.Games.Managers;
using Fmpostor.Api.Innersloth;
using Fmpostor.Api.Innersloth.GameOptions;
using Fmpostor.Api.Plugins;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Plugins.Example
{
    [FmpostorPlugin("gg.fmpostor.example")]
    public class ExamplePlugin : PluginBase
    {
        private readonly ILogger<ExamplePlugin> _logger;
        private readonly IGameManager _gameManager;

        public ExamplePlugin(ILogger<ExamplePlugin> logger, IGameManager gameManager)
        {
            _logger = logger;
            _gameManager = gameManager;
        }

        public override async ValueTask EnableAsync()
        {
            _logger.LogInformation("Example is being enabled.");

            var game = await _gameManager.CreateAsync(new NormalGameOptions(), GameFilterOptions.CreateDefault());
            if (game == null)
            {
                _logger.LogWarning("Example game creation was cancelled");
            }
            else
            {
                game.DisplayName = "Example game";
                await game.SetPrivacyAsync(true);

                _logger.LogInformation("Created game {0}.", game.Code.Code);
            }
        }

        public override ValueTask DisableAsync()
        {
            _logger.LogInformation("Example is being disabled.");
            return default;
        }
    }
}

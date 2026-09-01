using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.Http;

/// <summary>
/// Generate a diagnostic page to show that the Impostor HTTP server is working.
/// </summary>
[Route("/")]
public sealed class HelloController : ControllerBase
{
    private static bool _shownHello = false;
    private readonly ILogger<HelloController> _logger;

    public HelloController(ILogger<HelloController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult GetHello()
    {
        if (!_shownHello)
        {
            _shownHello = true;
            _logger.LogInformation("Fmpostor's Http server is reachable (this message is only printed once per start)");
        }

        return Ok(
            $"""
            Fmpostor AmongUs server is running normally.
            Thank you for your use and support!
            Current version: Turbo-640.0-20260901
            ============
            If you need server support, technical assistance, or would like to obtain the open-source address,
            please contact us via email: admin@fanchuanovo.cn
            Open-source repository: https://github.com/SailingBoat-ovo/Fmpostor
            """
        );
    }
}

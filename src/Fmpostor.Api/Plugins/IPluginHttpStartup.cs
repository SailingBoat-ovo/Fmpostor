using Microsoft.AspNetCore.Builder;

namespace Fmpostor.Api.Plugins;

public interface IPluginHttpStartup : IPluginStartup
{
    void ConfigureWebApplication(IApplicationBuilder builder);
}

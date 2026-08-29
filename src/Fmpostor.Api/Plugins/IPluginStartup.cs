using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fmpostor.Api.Plugins
{
    public interface IPluginStartup
    {
        void ConfigureHost(IHostBuilder host);

        void ConfigureServices(IServiceCollection services);
    }
}

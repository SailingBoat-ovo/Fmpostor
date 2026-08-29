using System.Collections.Generic;

namespace Fmpostor.Server.Plugins
{
    public class PluginConfig
    {
        public List<string> Paths { get; set; } = new List<string>();

        public List<string> LibraryPaths { get; set; } = new List<string>();
    }
}

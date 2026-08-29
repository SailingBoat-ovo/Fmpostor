using System.Reflection;
using System.Runtime.Loader;

namespace Fmpostor.Server.Plugins
{
    public interface IAssemblyInformation
    {
        AssemblyName AssemblyName { get; }

        bool IsPlugin { get; }

        Assembly Load(AssemblyLoadContext context);
    }
}

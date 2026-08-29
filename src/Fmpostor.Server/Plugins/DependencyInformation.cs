using Fmpostor.Api.Plugins;

namespace Fmpostor.Server.Plugins
{
    public class DependencyInformation
    {
        private readonly FmpostorDependencyAttribute _attribute;

        public DependencyInformation(FmpostorDependencyAttribute attribute)
        {
            _attribute = attribute;
        }

        public string Id => _attribute.Id;

        public DependencyType DependencyType => _attribute.DependencyType;
    }
}

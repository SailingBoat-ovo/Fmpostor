using System;

namespace Fmpostor.Api.Plugins
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class FmpostorDependencyAttribute : Attribute
    {
        public FmpostorDependencyAttribute(string id, DependencyType type)
        {
            Id = id;
            DependencyType = type;
        }

        public string Id { get; }

        public DependencyType DependencyType { get; }
    }
}

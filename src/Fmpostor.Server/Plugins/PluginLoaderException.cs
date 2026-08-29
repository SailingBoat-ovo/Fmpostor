using System;
using Fmpostor.Api;

namespace Fmpostor.Server.Plugins
{
    public class PluginLoaderException : FmpostorException
    {
        public PluginLoaderException()
        {
        }

        public PluginLoaderException(string? message) : base(message)
        {
        }

        public PluginLoaderException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}

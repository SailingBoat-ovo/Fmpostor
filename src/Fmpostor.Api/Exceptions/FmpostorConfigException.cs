using System;

namespace Fmpostor.Api
{
    public class FmpostorConfigException : FmpostorException
    {
        public FmpostorConfigException()
        {
        }

        public FmpostorConfigException(string? message) : base(message)
        {
        }

        public FmpostorConfigException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}

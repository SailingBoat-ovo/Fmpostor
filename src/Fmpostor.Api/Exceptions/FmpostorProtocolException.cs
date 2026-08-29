using System;

namespace Fmpostor.Api
{
    public class FmpostorProtocolException : FmpostorException
    {
        public FmpostorProtocolException()
        {
        }

        public FmpostorProtocolException(string? message) : base(message)
        {
        }

        public FmpostorProtocolException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}

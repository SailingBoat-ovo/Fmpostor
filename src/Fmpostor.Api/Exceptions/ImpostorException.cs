using System;

namespace Fmpostor.Api
{
    public class FmpostorException : Exception
    {
        public FmpostorException()
        {
        }

        public FmpostorException(string? message) : base(message)
        {
        }

        public FmpostorException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}

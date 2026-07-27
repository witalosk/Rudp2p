using System;

namespace Rudp2p
{
    /// <summary>
    /// Thrown when a reliable send could not be acknowledged by the receiver within the configured retry limits.
    /// </summary>
    public class Rudp2pSendException : Exception
    {
        public Rudp2pSendException(string message) : base(message) { }

        public Rudp2pSendException(string message, Exception innerException) : base(message, innerException) { }
    }
}

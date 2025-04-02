using System;

namespace Rudp2p
{
    /// <summary>
    /// Rudp2p configuration.
    /// </summary>
    [Serializable]
    public class Rudp2pConfig
    {
        /// <summary>
        /// Maximum Transmission Unit (Default: 1400)
        /// </summary>
        public int Mtu = 1400;

        /// <summary>
        /// if true, send packets in parallel (Default)
        /// if false, send packets in sequence (Low speed, but stable for some network)
        /// </summary>
        public bool ParallelSending = true;
    }
}
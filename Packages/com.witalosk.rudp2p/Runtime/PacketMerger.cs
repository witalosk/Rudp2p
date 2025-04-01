using System.Linq;

namespace Rudp2p
{
    internal class PacketMerger
    {
        private readonly object _lockObject = new();

        public bool IsComplete
        {
            get
            {
                lock (_lockObject)
                {
                    return _receivedFlags.All(f => f);
                }
            }
        }

        private readonly byte[][] _receivedPackets;
        private readonly bool[] _receivedFlags;

        public PacketMerger(int totalSeqNum)
        {
            _receivedPackets = new byte[totalSeqNum][];
            _receivedFlags = new bool[totalSeqNum];
        }

        public bool AddPacket(int seqNum, byte[] data)
        {
            lock (_lockObject)
            {
                if (_receivedFlags[seqNum] || seqNum < 0 || seqNum >= _receivedPackets.Length)
                {
                    return false;
                }
                _receivedPackets[seqNum] = data;
                _receivedFlags[seqNum] = true;
                return IsComplete;
            }
        }

        public byte[] GetMergedData()
        {
            lock (_lockObject)
            {
                return _receivedPackets.SelectMany(p => p).ToArray();
            }
        }
    }
}
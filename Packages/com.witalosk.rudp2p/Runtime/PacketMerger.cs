using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Rudp2p
{
    internal class PacketMerger
    {
        public bool IsComplete => _receivedFlags.All(f => f);
        
        private readonly byte[][] _receivedPackets;
        private readonly bool[] _receivedFlags;
        
        public PacketMerger(int totalSeqNum)
        {
            _receivedPackets = new byte[totalSeqNum][];
            _receivedFlags = new bool[totalSeqNum];
        }
        
        public bool AddPacket(int seqNum, byte[] data)
        {
            _receivedPackets[seqNum] = data;
            _receivedFlags[seqNum] = true;
            return IsComplete;
        }
        
        public byte[] GetMergedData()
        {
            return _receivedPackets.SelectMany(p => p).ToArray();
        }
    }
}
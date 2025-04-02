using System;

namespace Rudp2p
{
    internal class PacketMerger
    {
        public int ReceivedSize { get; private set; } = 0;

        private readonly ReadOnlyMemory<byte>[] _receivedPackets;
        private readonly ushort _totalSegNum = 0;
        private int _receivedCount = 0;

        private readonly object _lockObject = new object();

        public PacketMerger(ushort totalSegNum)
        {
            _totalSegNum = totalSegNum;
            _receivedPackets = new ReadOnlyMemory<byte>[_totalSegNum];
            ReceivedSize = 0;
        }

        public bool AddPacket(int seqNum, ReadOnlyMemory<byte> payload)
        {
            lock (_lockObject)
            {
                if (seqNum < 0 || seqNum >= _totalSegNum || !_receivedPackets[seqNum].IsEmpty)
                {
                    return false;
                }

                _receivedPackets[seqNum] = payload;
                ReceivedSize += payload.Length;

                return ++_receivedCount == _totalSegNum;
            }
        }

        public void SetMergedData(Span<byte> target)
        {
            lock (_lockObject)
            {
                int offset = 0;
                foreach (var packet in _receivedPackets)
                {
                    if (packet.IsEmpty) return;

                    packet.Span.CopyTo(target[offset..]);
                    offset += packet.Length;
                }
            }
        }
    }
}
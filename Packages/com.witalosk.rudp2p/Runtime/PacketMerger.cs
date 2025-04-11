using System;
using System.Buffers;

namespace Rudp2p
{
    internal class PacketMerger : IDisposable
    {
        private struct ReceivedPacketData
        {
            public byte[] Data;
            public int Length;
        }

        public int ReceivedSize { get; private set; } = 0;

        private readonly ReceivedPacketData[] _receivedPackets;
        private readonly ushort _totalSegNum = 0;
        private int _receivedCount = 0;
        private bool _isDisposed = false;

        private readonly object _lockObject = new object();

        public PacketMerger(ushort totalSegNum)
        {
            _totalSegNum = totalSegNum;
            _receivedPackets = new ReceivedPacketData[totalSegNum];
            ReceivedSize = 0;
        }

        public bool AddPacket(int seqNum, ReadOnlyMemory<byte> payload)
        {
            lock (_lockObject)
            {
                if (_isDisposed || seqNum < 0 || seqNum >= _totalSegNum)
                {
                    return false;
                }

                byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
                try
                {
                    payload.Span.CopyTo(buffer);
                    _receivedPackets[seqNum].Data = buffer;
                    _receivedPackets[seqNum].Length = payload.Length;
                    ReceivedSize += payload.Length;
                }
                catch (Exception)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }

                return ++_receivedCount == _totalSegNum;
            }
        }

        public void SetMergedData(Span<byte> target)
        {
            lock (_lockObject)
            {
                if (_isDisposed) throw new ObjectDisposedException(nameof(PacketMerger));

                int offset = 0;
                foreach (var packet in _receivedPackets)
                {
                    var sourceSpan = new Span<byte>(packet.Data, 0, packet.Length);
                    sourceSpan.CopyTo(target[offset..]);
                    offset += packet.Length;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            lock (_lockObject)
            {
                if (_isDisposed) return;

                if (disposing)
                {
                    foreach (var packet in _receivedPackets)
                    {
                        if (packet.Data != null)
                        {
                            ArrayPool<byte>.Shared.Return(packet.Data);
                        }
                    }
                    ReceivedSize = 0;
                    _receivedCount = 0;
                }

                _isDisposed = true;
            }
        }

        ~PacketMerger()
        {
            Dispose(false);
        }
    }
}
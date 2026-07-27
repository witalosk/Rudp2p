using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Random = System.Random;

namespace Rudp2p
{
    internal class ReliableSender : IDisposable
    {
        private static int _packetIdCounter = new Random().Next();

        private readonly SendQueue _sendQueue;
        private readonly ConcurrentDictionary<int, bool[]> _ackReceived = new();
        private readonly Rudp2pConfig _config;

        private const int _defaultBucketSize = 3000000;
        private const int _defaultRefillRate = 1875000;

        internal ReliableSender(Rudp2pConfig config)
        {
            _config = config;
            _sendQueue = new SendQueue(_defaultBucketSize, _defaultRefillRate);
        }

        internal ReliableSender(Rudp2pConfig config, SendQueue sendQueue)
        {
            _config = config;
            _sendQueue = sendQueue;
        }

        public async Task SendAsync(Socket socket, IPEndPoint target, int key, ReadOnlyMemory<byte> data, bool isReliable = true)
        {
            if (data.Length > _config.Mtu * ushort.MaxValue - PacketHeader.Size)
            {
                throw new Exception($"Data is too large to send (Max size: {_config.Mtu * ushort.MaxValue - PacketHeader.Size} bytes)");
            }

            int packetId = Interlocked.Increment(ref _packetIdCounter);
            int singlePayloadSize = _config.Mtu - PacketHeader.Size;
            int totalPackets = (data.Length + singlePayloadSize - 1) / singlePayloadSize;

            bool[] ackReceived = null;
            if (isReliable)
            {
                ackReceived = new bool[totalPackets];
                _ackReceived[packetId] = ackReceived;
            }

            List<byte[]> sendBuffers = new(totalPackets);

            try
            {
                List<Task> tasks = new();
                for (int i = 0; i < totalPackets; i++)
                {
                    byte[] sendBuffer = ArrayPool<byte>.Shared.Rent(_config.Mtu);
                    sendBuffers.Add(sendBuffer);
                    var sendBufferSegment = new ArraySegment<byte>(sendBuffer, 0, _config.Mtu);

                    int srcOffset = i * singlePayloadSize;
                    int payloadSize = Math.Min(singlePayloadSize, data.Length - srcOffset);

                    PacketHelper.SetHeader(sendBufferSegment, new PacketHeader(packetId, (ushort)i, (ushort)totalPackets, key));
                    data.Span.Slice(srcOffset, payloadSize).CopyTo(sendBufferSegment[PacketHeader.Size..]);

                    if (_config.ParallelSending)
                    {
                        tasks.Add
                        (
                            isReliable
                                ? SendWithRetry(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)], i, ackReceived)
                                : SendOrEnqueue(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)])
                        );
                    }
                    else
                    {
                        await (isReliable
                            ? SendWithRetry(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)], i, ackReceived)
                            : SendOrEnqueue(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)]));
                    }
                }

                if (_config.ParallelSending) { await Task.WhenAll(tasks); }
            }
            finally
            {
                foreach (byte[] sendBuffer in sendBuffers)
                {
                    ArrayPool<byte>.Shared.Return(sendBuffer);
                }
                if (isReliable)
                {
                    _ackReceived.TryRemove(packetId, out _);
                }
            }
        }

        public void ReportAck(int packetId, int seq)
        {
            if (!_ackReceived.TryGetValue(packetId, out bool[] value)) return;
            if ((uint)seq >= (uint)value.Length) return;
            value[seq] = true;
        }

        public void Dispose()
        {
            _sendQueue?.Dispose();
        }

        private async Task SendWithRetry(Socket client, IPEndPoint target, ArraySegment<byte> packet, int seq, bool[] ackReceived)
        {
            for (int tryNum = 0; tryNum < _config.ReliableRetryCount; tryNum++)
            {
                await SendOrEnqueue(client, target, packet);

                int elapsedMs = 0;
                while (!ackReceived[seq] && elapsedMs < _config.ReliableRetryInterval)
                {
                    await Task.Delay(1);
                    elapsedMs += 1;
                }

                if (ackReceived[seq]) return;
            }

            throw new Rudp2pSendException($"Packet fragment {seq} was not acknowledged after {_config.ReliableRetryCount} attempts");
        }

        private Task SendOrEnqueue(Socket client, IPEndPoint target, ArraySegment<byte> data)
        {
            return _config.EnableSendRateLimitByBucket
                ? _sendQueue.Enqueue(client, target, data)
                : client.SendToAsync(data, SocketFlags.None, target);
        }
    }
}
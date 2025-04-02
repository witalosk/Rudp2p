using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Random = System.Random;

namespace Rudp2p
{
    internal class ReliableSender : IDisposable
    {
        private readonly SendQueue _sendQueue = new(1250000, 625000);
        private readonly Dictionary<int, bool[]> _ackReceived = new();

        public async Task SendAsync(Socket socket, IPEndPoint target, int key, ReadOnlyMemory<byte> data, Rudp2pConfig config, bool isReliable = true)
        {
            if (data.Length > config.Mtu * ushort.MaxValue - PacketHeader.Size)
            {
                throw new Exception($"Data is too large to send (Max size: {config.Mtu * ushort.MaxValue - PacketHeader.Size} bytes)");
            }

            int packetId = new Random().Next();
            int singlePayloadSize = config.Mtu - PacketHeader.Size;
            int totalPackets = (data.Length + singlePayloadSize - 1) / singlePayloadSize;
            _ackReceived[packetId] = ArrayPool<bool>.Shared.Rent(totalPackets);
            List<byte[]> sendBuffers = new();

            try
            {
                List<Task> tasks = new();
                for (int i = 0; i < totalPackets; i++)
                {
                    byte[] sendBuffer = ArrayPool<byte>.Shared.Rent(config.Mtu);
                    sendBuffers.Add(sendBuffer);
                    var sendBufferSegment = new ArraySegment<byte>(sendBuffer, 0, config.Mtu);

                    int srcOffset = i * singlePayloadSize;
                    int payloadSize = Math.Min(singlePayloadSize, data.Length - srcOffset);

                    PacketHelper.SetHeader(sendBufferSegment, new PacketHeader(packetId, (ushort)i, (ushort)totalPackets, key));
                    data.Span.Slice(srcOffset, payloadSize).CopyTo(sendBufferSegment[PacketHeader.Size..]);

                    if (config.ParallelSending)
                    {
                        tasks.Add
                        (
                            isReliable
                                ? SendWithRetry(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)], i, _ackReceived[packetId], config)
                                : _sendQueue.Enqueue(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)])
                        );
                    }
                    else
                    {
                        await (isReliable
                            ? SendWithRetry(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)], i, _ackReceived[packetId], config)
                            : _sendQueue.Enqueue(socket, target, sendBufferSegment[..(payloadSize + PacketHeader.Size)]));
                    }
                }

                if (config.ParallelSending) { await Task.WhenAll(tasks); }
            }
            finally
            {
                foreach (byte[] sendBuffer in sendBuffers)
                {
                    ArrayPool<byte>.Shared.Return(sendBuffer);
                }
                ArrayPool<bool>.Shared.Return(_ackReceived[packetId]);
                _ackReceived.Remove(packetId);
            }
        }

        public void ReportAck(int packetId, int seq)
        {
            if (!_ackReceived.TryGetValue(packetId, out bool[] value)) return;
            value[seq] = true;
        }

        public void Dispose()
        {
            _sendQueue?.Dispose();
        }

        private async Task SendWithRetry(Socket client, IPEndPoint target, ArraySegment<byte> packet, int seq, bool[] ackReceived, Rudp2pConfig config)
        {
            for (int tryNum = 0; tryNum < config.ReliableRetryCount; tryNum++)
            {
                await _sendQueue.Enqueue(client, target, packet);

                int elapsedMs = 0;
                while (!ackReceived[seq] && elapsedMs < config.ReliableRetryInterval)
                {
                    await Task.Delay(1);
                    elapsedMs += 1;
                }

                if (ackReceived[seq]) break;

                if (tryNum == config.ReliableRetryCount - 1)
                {
                    Console.WriteLine($"[WARN] Packet {seq} lost after {config.ReliableRetryCount} attempts");
                }
            }
        }


    }
}
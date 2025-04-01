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

        public async Task Send(UdpClient udp, IPEndPoint target, int key, byte[] data, int mtu = 1500, bool isReliable = true)
        {
            if (data.Length > mtu * ushort.MaxValue - Packet.HeaderSize)
            {
                throw new Exception($"Data is too large to send (Max size: {mtu * ushort.MaxValue - Packet.HeaderSize} bytes)");
            }

            int packetId = new Random().Next();
            int packetSize = mtu - Packet.HeaderSize;
            int totalPackets = (data.Length + packetSize - 1) / packetSize;
            _ackReceived[packetId] = new bool[totalPackets];

            for (int i = 0; i < totalPackets; i++)
            {
                int offset = i * packetSize;
                int size = Math.Min(packetSize, data.Length - offset);
                int arraySize = size + Packet.HeaderSize;

                byte[] packet = ArrayPool<byte>.Shared.Rent(arraySize);
                Packet.SetHeader(ref packet, packetId, (ushort)i, (ushort)totalPackets, key);
                Buffer.BlockCopy(data, offset, packet, Packet.HeaderSize, size);

                if (isReliable)
                {
                    await SendWithRetry(udp, packet, arraySize, target, i, _ackReceived[packetId]);
                }
                else
                {
                    await _sendQueue.Enqueue(udp, target, packet, arraySize);
                }

                ArrayPool<byte>.Shared.Return(packet);
            }

            _ackReceived.Remove(packetId);
        }

        public void ReportAck(int packetId, int seq)
        {
            _ackReceived[packetId][seq] = true;
        }

        public void Dispose()
        {
            _sendQueue?.Dispose();
        }

        private async Task SendWithRetry(UdpClient udp, byte[] packet, int packetSize, IPEndPoint target, int seq, bool[] ackReceived)
        {
            for (int tryNum = 0; tryNum < 5; tryNum++)
            {
                await _sendQueue.Enqueue(udp, target, packet, packetSize);

                int elapsedMs = 0;
                while (!ackReceived[seq] && elapsedMs < 50)
                {
                    await Task.Delay(1);
                    elapsedMs += 1;
                }

                if (ackReceived[seq]) break;

                if (tryNum == 4)
                {
                    Console.WriteLine($"[WARN] Packet {seq} lost after 5 attempts");
                }
            }
        }


    }
}
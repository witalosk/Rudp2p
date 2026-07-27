using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>[]> _ackWaiters = new();
        private readonly Rudp2pConfig _config;
        private readonly SemaphoreSlim _sendWindow;

        // Smoothed RTT state (RFC 6298), guarded by _rttLock
        private readonly object _rttLock = new();
        private double _srttMs = -1;
        private double _rttVarMs;

        private const int _minRtoMs = 10;
        private const int _maxRtoMs = 2000;
        private const int _defaultBucketSize = 3000000;
        private const int _defaultRefillRate = 1875000;

        internal ReliableSender(Rudp2pConfig config) : this(config, new SendQueue(_defaultBucketSize, _defaultRefillRate)) { }

        internal ReliableSender(Rudp2pConfig config, SendQueue sendQueue)
        {
            _config = config;
            _sendQueue = sendQueue;
            _sendWindow = new SemaphoreSlim(Math.Max(1, config.SendWindowSize));
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

            TaskCompletionSource<bool>[] ackWaiters = null;
            if (isReliable)
            {
                ackWaiters = new TaskCompletionSource<bool>[totalPackets];
                for (int i = 0; i < totalPackets; i++)
                {
                    ackWaiters[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _ackWaiters[packetId] = ackWaiters;
            }

            List<byte[]> sendBuffers = new(totalPackets);

            try
            {
                List<Task> tasks = _config.ParallelSending ? new List<Task>(totalPackets) : null;
                for (int i = 0; i < totalPackets; i++)
                {
                    byte[] sendBuffer = ArrayPool<byte>.Shared.Rent(_config.Mtu);
                    sendBuffers.Add(sendBuffer);
                    var sendBufferSegment = new ArraySegment<byte>(sendBuffer, 0, _config.Mtu);

                    int srcOffset = i * singlePayloadSize;
                    int payloadSize = Math.Min(singlePayloadSize, data.Length - srcOffset);

                    PacketHelper.SetHeader(sendBufferSegment, new PacketHeader(PacketType.Data, packetId, (ushort)i, (ushort)totalPackets, key));
                    data.Span.Slice(srcOffset, payloadSize).CopyTo(sendBufferSegment[PacketHeader.Size..]);

                    var packet = sendBufferSegment[..(payloadSize + PacketHeader.Size)];

                    if (_config.ParallelSending)
                    {
                        tasks!.Add(SendFragmentWithWindow(socket, target, packet, i, ackWaiters, isReliable));
                    }
                    else
                    {
                        await (isReliable
                            ? SendWithRetry(socket, target, packet, i, ackWaiters)
                            : SendOrEnqueue(socket, target, packet));
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
                    _ackWaiters.TryRemove(packetId, out _);
                }
            }
        }

        public void ReportAck(int packetId, int seq)
        {
            if (!_ackWaiters.TryGetValue(packetId, out TaskCompletionSource<bool>[] waiters)) return;
            if ((uint)seq >= (uint)waiters.Length) return;
            waiters[seq].TrySetResult(true);
        }

        public void Dispose()
        {
            _sendQueue?.Dispose();
        }

        private async Task SendFragmentWithWindow(Socket socket, IPEndPoint target, ArraySegment<byte> packet, int seq, TaskCompletionSource<bool>[] ackWaiters, bool isReliable)
        {
            // Caps the number of in-flight fragments so large payloads do not flood
            // the network and trigger self-inflicted packet loss
            await _sendWindow.WaitAsync();
            try
            {
                if (isReliable)
                {
                    await SendWithRetry(socket, target, packet, seq, ackWaiters);
                }
                else
                {
                    await SendOrEnqueue(socket, target, packet);
                }
            }
            finally
            {
                _sendWindow.Release();
            }
        }

        private async Task SendWithRetry(Socket client, IPEndPoint target, ArraySegment<byte> packet, int seq, TaskCompletionSource<bool>[] ackWaiters)
        {
            Task<bool> ackTask = ackWaiters[seq].Task;
            int rto = GetCurrentRtoMs();

            for (int tryNum = 0; tryNum < _config.ReliableRetryCount; tryNum++)
            {
                long sentAt = Stopwatch.GetTimestamp();
                await SendOrEnqueue(client, target, packet);

                if (await Task.WhenAny(ackTask, Task.Delay(rto)) == ackTask)
                {
                    // Karn's algorithm: only sample RTT for fragments that were not retransmitted
                    if (tryNum == 0)
                    {
                        ReportRttSample((Stopwatch.GetTimestamp() - sentAt) * 1000.0 / Stopwatch.Frequency);
                    }
                    return;
                }

                rto = Math.Min(rto * 2, _maxRtoMs);
            }

            throw new Rudp2pSendException($"Packet fragment {seq} was not acknowledged after {_config.ReliableRetryCount} attempts");
            
            int GetCurrentRtoMs()
            {
                lock (_rttLock)
                {
                    if (_srttMs < 0) return Math.Max(_minRtoMs, _config.ReliableRetryInterval);
                    return Math.Clamp((int)(_srttMs + 4.0 * _rttVarMs), _minRtoMs, _maxRtoMs);
                }
            }

            void ReportRttSample(double rttMs)
            {
                lock (_rttLock)
                {
                    if (_srttMs < 0)
                    {
                        _srttMs = rttMs;
                        _rttVarMs = rttMs / 2.0;
                    }
                    else
                    {
                        _rttVarMs = 0.75 * _rttVarMs + 0.25 * Math.Abs(_srttMs - rttMs);
                        _srttMs = 0.875 * _srttMs + 0.125 * rttMs;
                    }
                }
            }
        }


        private Task SendOrEnqueue(Socket client, IPEndPoint target, ArraySegment<byte> data)
        {
            return _config.EnableSendRateLimitByBucket
                ? _sendQueue.Enqueue(client, target, data)
                : client.SendToAsync(data, SocketFlags.None, target);
        }
    }
}

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Rudp2p
{
    /// <summary>
    /// Reliable, unordered, datagram protocol for peer-to-peer communication
    /// </summary>
    public class Rudp2pClient : IDisposable
    {
        public Rudp2pConfig Config { get; } = new();
        public Socket Socket { get; private set; }

        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private Task _cleanupTask;
        private ConcurrentDictionary<(IPEndPoint Sender, int PacketId), PacketMerger> _packetMergers;

        private readonly ConcurrentDictionary<int, Action<Rudp2pReceiveData>[]> _callbacks = new();
        private readonly object _callbackLock = new();

        private ReliableSender _reliableSender;

        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan _processedIdTimeout = TimeSpan.FromSeconds(1);
        private readonly TimeSpan _incompleteMergerTimeout = TimeSpan.FromSeconds(5);
        private readonly ConcurrentDictionary<(IPEndPoint Sender, int PacketId), DateTime> _processedPacketIds = new();

        private const int _closeWaitTimeoutMs = 1000;

        public Rudp2pClient() { }

        public Rudp2pClient(Rudp2pConfig config)
        {
            Config = config;
        }

        public void Start(int port)
        {
            Close();

            try
            {
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (Exception)
            {
                Socket?.Dispose();
                Socket = null;
                throw;
            }

            _packetMergers = new ConcurrentDictionary<(IPEndPoint, int), PacketMerger>();
            _reliableSender = new ReliableSender(Config, new SendQueue(Config.SendBucketByteSize, Config.SendBucketRefillRate));
            _processedPacketIds.Clear();

            _cts = new CancellationTokenSource();

            // The loops capture the socket locally so a subsequent Close() + Start()
            // can never make an old loop observe the new socket
            Socket socket = Socket;
            _receiveTask = Task.Run(() => ReceiveLoop(socket, _cts.Token));
            _cleanupTask = Task.Run(() => CleanupLoopProcessedPacketIds(_cts.Token));
        }

        public void Close()
        {
            _cts?.Cancel();
            _reliableSender?.Dispose();
            _reliableSender = null;
            Socket?.Dispose();
            Socket = null;

            // Wait for the loops to exit so Start() never runs concurrently with old loops.
            // Disposing the socket aborts the pending receive, so this returns quickly.
            try
            {
                if (_receiveTask != null && _cleanupTask != null)
                {
                    Task.WhenAll(_receiveTask, _cleanupTask).Wait(_closeWaitTimeoutMs);
                }
            }
            catch (AggregateException)
            {
                // Loop exceptions are already logged inside the loops
            }
            _receiveTask = null;
            _cleanupTask = null;

            _cts?.Dispose();
            _cts = null;

            // Release fragment buffers held by unfinished mergers
            var mergers = _packetMergers;
            if (mergers != null)
            {
                foreach (var pair in mergers)
                {
                    pair.Value.Dispose();
                }
                mergers.Clear();
            }
        }

        public void Dispose()
        {
            Close();
        }

        /// <summary>
        /// Register a callback to receive data with the specified key.
        /// The callback runs on the receive-loop thread, and <see cref="Rudp2pReceiveData.Data"/>
        /// is only valid until the callback returns (copy it to keep it).
        /// </summary>
        public IDisposable RegisterCallback(int key, Action<Rudp2pReceiveData> callback)
        {
            lock (_callbackLock)
            {
                if (_callbacks.TryGetValue(key, out var existing))
                {
                    var updated = new Action<Rudp2pReceiveData>[existing.Length + 1];
                    existing.CopyTo(updated, 0);
                    updated[existing.Length] = callback;
                    _callbacks[key] = updated;
                }
                else
                {
                    _callbacks[key] = new[] { callback };
                }
            }

            return new CallbackDisposer(this, callback);
        }

        /// <summary>
        /// Unregister a callback
        /// </summary>
        public void UnregisterCallback(Action<Rudp2pReceiveData> callback)
        {
            lock (_callbackLock)
            {
                foreach (var pair in _callbacks)
                {
                    int index = Array.IndexOf(pair.Value, callback);
                    if (index < 0) continue;

                    var updated = new Action<Rudp2pReceiveData>[pair.Value.Length - 1];
                    Array.Copy(pair.Value, 0, updated, 0, index);
                    Array.Copy(pair.Value, index + 1, updated, index, pair.Value.Length - index - 1);
                    _callbacks[pair.Key] = updated;
                }
            }
        }

        /// <summary>
        /// Send data to the target endpoint asynchronously.
        /// Throws <see cref="Rudp2pSendException"/> if a reliable send is not acknowledged within the retry limits.
        /// </summary>
        /// <param name="target">Target endpoint</param>
        /// <param name="key">Key to identify the data (User defined)</param>
        /// <param name="data">Data to send</param>
        /// <param name="isReliable">Whether to use reliable transmission</param>
        /// <param name="cancellationToken">Cancels the send, including pending retries. Close() also cancels all in-flight sends.</param>
        public async Task SendAsync(IPEndPoint target, int key, ReadOnlyMemory<byte> data, bool isReliable = true, CancellationToken cancellationToken = default)
        {
            ReliableSender sender = _reliableSender;
            CancellationTokenSource cts = _cts;
            Socket socket = Socket;
            if (sender == null || cts == null || socket == null)
            {
                throw new InvalidOperationException("Client is not started. Call Start() first.");
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            await sender.SendAsync(socket, target, key, data, isReliable, linkedCts.Token);
        }

        private async Task ReceiveLoop(Socket socket, CancellationToken token)
        {
            byte[] receiveBuffer = new byte[Config.Mtu + 100];
            ArraySegment<byte> receiveSegment = new(receiveBuffer);
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(receiveSegment, SocketFlags.None, remoteEndPoint);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException se)
                {
                    switch (se.SocketErrorCode)
                    {
                        // Ignored errors
                        case SocketError.ConnectionReset: // ICMP Port Unreachable
                        case SocketError.MessageSize: // Over MTU
                        case SocketError.TimedOut: // Timeout
                        case SocketError.NetworkReset: // Network dropped connection on reset
                        case SocketError.NetworkUnreachable: // Network unreachable
                            continue;

                        // Expected errors (the socket is no longer usable, exit the loop)
                        case SocketError.Interrupted: // Interrupted by Close()
                        case SocketError.OperationAborted: // Cancellation requested
                        case SocketError.Shutdown: // Shutdown
                        case SocketError.NotSocket: // Invalid Socket
                            return;

                        // Unexpected errors
                        default:
                            OutputLog($"Socket Error Code: {se.SocketErrorCode}, Message: {se.Message}");
                            await Task.Delay(1, token);
                            continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    OutputLog(e.ToString());
                    await Task.Delay(1, token);
                    continue;
                }

                if (result.ReceivedBytes < PacketHeader.Size) continue;

                try
                {
                    OnReceiveData(receiveSegment.AsMemory(0, result.ReceivedBytes), result.RemoteEndPoint as IPEndPoint);
                }
                catch (Exception e)
                {
                    OutputLog(e.ToString());
                }
            }
        }

        private void OnReceiveData(ReadOnlyMemory<byte> data, IPEndPoint sender)
        {
            // Drop datagrams that are not Rudp2p traffic (wrong magic number or unknown type)
            if (!PacketHelper.TryGetHeader(data.Span, out var header)) return;

            if (header.Type == PacketType.Ack)
            {
                _reliableSender.ReportAck(sender, header.PacketId, header.SeqId);
                return;
            }

            // Malformed data packet
            if (header.TotalSeqNum == 0 || header.SeqId >= header.TotalSeqNum) return;

            var mergerKey = (sender, header.PacketId);

            // Only reliable packets expect an ACK; suppressing it for unreliable
            // streams saves the return bandwidth
            bool requiresAck = header.Type == PacketType.Data;

            if (_processedPacketIds.ContainsKey(mergerKey))
            {
                if (requiresAck) SendAck(sender, header.PacketId, header.SeqId);
                return;
            }
            if (requiresAck) SendAck(sender, header.PacketId, header.SeqId);

            var payload = PacketHelper.GetPayload(data);
            var packetMerger = _packetMergers.GetOrAdd(mergerKey, static (_, totalSeqNum) => new PacketMerger(totalSeqNum), header.TotalSeqNum);

            if (packetMerger.AddPacket(header.SeqId, payload))
            {
                _processedPacketIds.TryAdd(mergerKey, DateTime.UtcNow);

                using (var owner = MemoryPool<byte>.Shared.Rent(packetMerger.ReceivedSize))
                {
                    packetMerger.SetMergedData(owner.Memory.Span);

                    try
                    {
                        if (_callbacks.TryGetValue(header.Key, out Action<Rudp2pReceiveData>[] callbacks))
                        {
                            var receiveData = new Rudp2pReceiveData { RemoteEndPoint = sender, Data = owner.Memory[..packetMerger.ReceivedSize] };

                            foreach (var callback in callbacks)
                            {
                                callback(receiveData);
                            }
                        }

                        _packetMergers.TryRemove(mergerKey, out _);
                    }
                    finally
                    {
                        packetMerger.Dispose();
                    }
                }
            }
        }

        private void SendAck(IPEndPoint sender, int packetId, int seq)
        {
            Socket socket = Socket;
            if (socket == null) return;

            byte[] ackPacket = ArrayPool<byte>.Shared.Rent(PacketHeader.Size);
            PacketHelper.SetHeader(ackPacket, new PacketHeader(PacketType.Ack, packetId, (ushort)seq, 0, 0));
            _ = SendAckAsync(socket, ackPacket, sender);
        }

        private async Task SendAckAsync(Socket socket, byte[] ackPacket, IPEndPoint sender)
        {
            try
            {
                await socket.SendToAsync(new ArraySegment<byte>(ackPacket, 0, PacketHeader.Size), SocketFlags.None, sender);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException se)
            {
                switch (se.SocketErrorCode)
                {
                    // Ignored errors
                    case SocketError.ConnectionReset: // ICMP Port Unreachable
                    case SocketError.NetworkUnreachable: // Network unreachable
                        break;

                    // Unexpected errors
                    default:
                        OutputLog($"Socket Error Code: {se.SocketErrorCode}, Message: {se.Message}");
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(ackPacket);
            }
        }

        private async Task CleanupLoopProcessedPacketIds(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_cleanupInterval, token);

                    DateTime now = DateTime.UtcNow;
                    DateTime cutoffTime = now - _processedIdTimeout;

                    foreach (var pair in _processedPacketIds)
                    {
                        if (pair.Value < cutoffTime)
                        {
                            _processedPacketIds.TryRemove(pair.Key, out _);
                        }
                    }
                    
                    var mergers = _packetMergers;
                    if (mergers == null) continue;

                    DateTime mergerCutoffTime = now - _incompleteMergerTimeout;
                    foreach (var pair in mergers)
                    {
                        if (pair.Value.LastReceivedUtc < mergerCutoffTime && mergers.TryRemove(pair.Key, out var merger))
                        {
                            merger.Dispose();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void OutputLog(string message)
        {
            // UnityEngine.Debug is thread-safe, so no SynchronizationContext dispatch is needed
#if UNITY_EDITOR
            Debug.Log(message);
#elif UNITY_5_3_OR_NEWER
            Debug.LogWarning(message);
#else
            Console.WriteLine(message);
#endif
        }

        public class CallbackDisposer : IDisposable
        {
            private readonly Rudp2pClient _parent;
            private readonly Action<Rudp2pReceiveData> _callback;

            public CallbackDisposer(Rudp2pClient parent, Action<Rudp2pReceiveData> callback)
            {
                _parent = parent;
                _callback = callback;
            }

            public void Dispose()
            {
                _parent.UnregisterCallback(_callback);
            }
        }

        public struct Rudp2pReceiveData
        {
            public IPEndPoint RemoteEndPoint;

            /// <summary>
            /// Received payload. This memory is pooled and returned to the pool as soon as
            /// the callback returns — copy it (e.g. Data.ToArray()) if you need to keep it
            /// beyond the callback, such as when dispatching to another thread.
            /// </summary>
            public ReadOnlyMemory<byte> Data;
        }
    }
}
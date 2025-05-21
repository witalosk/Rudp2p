using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private IPEndPoint _remoteEndPoint;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<int, PacketMerger> _packetMergers;
        private readonly Dictionary<int, List<Action<Rudp2pReceiveData>>> _callbacks = new();
        private ReliableSender _reliableSender;
        private SynchronizationContext _originalContext;

        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan _processedIdTimeout = TimeSpan.FromSeconds(1);
        private CancellationTokenSource _cleanupCts;
        private readonly ConcurrentDictionary<int, DateTime> _processedPacketIds = new();

        public Rudp2pClient() { }

        public Rudp2pClient(Rudp2pConfig config)
        {
            Config = config;
        }

        public void Start(int port)
        {
            if (Socket != null)
            {
                Close();
            }

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

            _packetMergers = new ConcurrentDictionary<int, PacketMerger>();
            _reliableSender = new ReliableSender(new SendQueue(Config.SendBucketByteSize, Config.SendBucketRefillRate));
            _originalContext = SynchronizationContext.Current;
            _processedPacketIds.Clear();

            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            Task.Run(() => ReceiveLoop(_cts.Token));

            if (_cleanupCts != null) return;
            _cleanupCts = new CancellationTokenSource();
            Task.Run(() => CleanupLoopProcessedPacketIds(_cleanupCts.Token));
        }

        public void Close()
        {
            _reliableSender?.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            Socket?.Dispose();
            Socket = null;
            _cleanupCts?.Cancel();
            _cleanupCts?.Dispose();
            _cleanupCts = null;
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        ~Rudp2pClient()
        {
            Close();
        }

        /// <summary>
        /// Register a callback to receive data with the specified key
        /// </summary>
        public IDisposable RegisterCallback(int key, Action<Rudp2pReceiveData> callback)
        {
            if (!_callbacks.ContainsKey(key))
            {
                _callbacks[key] = new List<Action<Rudp2pReceiveData>>();
            }

            _callbacks[key].Add(callback);
            return new CallbackDisposer(this, callback);
        }

        /// <summary>
        /// Unregister a callback
        /// </summary>
        public void UnregisterCallback(Action<Rudp2pReceiveData> callback)
        {
            foreach (int key in _callbacks.Keys)
            {
                _callbacks[key].Remove(callback);
            }
        }

        /// <summary>
        /// Send data to the target endpoint asynchronously
        /// </summary>
        /// <param name="target">Target endpoint</param>
        /// <param name="key">Key to identify the data (User defined)</param>
        /// <param name="data">Data to send</param>
        /// <param name="isReliable">Whether to use reliable transmission</param>
        public Task SendAsync(IPEndPoint target, int key, ReadOnlyMemory<byte> data, bool isReliable = true)
        {
            return _reliableSender.SendAsync(Socket, target, key, data, Config, isReliable);
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] receiveBuffer = new byte[Config.Mtu + 100];
            ArraySegment<byte> receiveSegment = new(receiveBuffer);
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await Socket.ReceiveFromAsync(receiveSegment, SocketFlags.None, remoteEndPoint);
                    if (result.ReceivedBytes < PacketHeader.Size) continue;

                    OnReceiveData(receiveSegment.AsMemory(0, result.ReceivedBytes), result.RemoteEndPoint as IPEndPoint);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    OutputLog(e.ToString());
                    await Task.Delay(500, token);
                }

            }
        }

        private void OnReceiveData(ReadOnlyMemory<byte> data, IPEndPoint sender)
        {
            var header = PacketHelper.GetHeader(data.Span);

            if (header.TotalSeqNum == 0)
            {
                // Ack Received
                _reliableSender.ReportAck(header.PacketId, header.SeqId);
                return;
            }

            if (_processedPacketIds.ContainsKey(header.PacketId))
            {
                SendAck(sender, header.PacketId, header.SeqId);
                return;
            }
            SendAck(sender, header.PacketId, header.SeqId);

            var payload = PacketHelper.GetPayload(data);
            var packetMerger = _packetMergers.GetOrAdd(header.PacketId, _ => new PacketMerger(header.TotalSeqNum));

            if (packetMerger.AddPacket(header.SeqId, payload))
            {
                _processedPacketIds.TryAdd(header.PacketId, DateTime.Now);

                using (var owner = MemoryPool<byte>.Shared.Rent(packetMerger.ReceivedSize))
                {
                    packetMerger.SetMergedData(owner.Memory.Span);

                    try
                    {
                        if (_callbacks.TryGetValue(header.Key, out List<Action<Rudp2pReceiveData>> callbacks))
                        {
                            var receiveData = new Rudp2pReceiveData { RemoteEndPoint = sender, Data = owner.Memory[..packetMerger.ReceivedSize] };

                            foreach (var callback in callbacks)
                            {
                                callback(receiveData);
                            }
                        }

                        _packetMergers.TryRemove(header.PacketId, out _);
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
            byte[] ackPacket = ArrayPool<byte>.Shared.Rent(PacketHeader.Size);
            try
            {
                PacketHelper.SetHeader(ackPacket, new PacketHeader(packetId, (ushort)seq, 0, 0));
                Socket.SendTo(ackPacket, ackPacket.Length, SocketFlags.None, sender);
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

                    DateTime cutoffTime = DateTime.Now - _processedIdTimeout;

                    foreach (var pair in _processedPacketIds)
                    {
                        if (pair.Value < cutoffTime)
                        {
                            _processedPacketIds.TryRemove(pair.Key, out _);
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
#if UNITY_EDITOR
            Debug.Log(message);
#elif UNITY_5_3_OR_NEWER
            _originalContext.Post(_ => Debug.LogWarning(message), null);
#else
            _originalContext.Post(_ => Console.WriteLine(message), null);
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
            public ReadOnlyMemory<byte> Data;
        }
    }
}
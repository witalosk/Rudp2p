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
        private Socket _socket;
        private IPEndPoint _remoteEndPoint;
        private int _port;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<int, PacketMerger> _packetMergers;
        private readonly Dictionary<int, List<Action<ReadOnlyMemory<byte>>>> _callbacks = new();
        private ReliableSender _reliableSender;
        private SynchronizationContext _originalContext;

        private readonly TimeSpan _cleanupInterval = TimeSpan.FromMilliseconds(100);
        private readonly TimeSpan _processedIdTimeout = TimeSpan.FromSeconds(1);
        private CancellationTokenSource _cleanupCts;
        private readonly ConcurrentDictionary<int, DateTime> _processedPacketIds = new();

        private Rudp2pConfig _config = new();

        public Rudp2pClient() { }

        public Rudp2pClient(Rudp2pConfig config)
        {
            _config = config;
        }

        public void Start(int port)
        {
            if (_socket != null)
            {
                Close();
            }

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch (Exception)
            {
                _socket?.Dispose();
                _socket = null;
                throw;
            }

            _port = port;
            _packetMergers = new ConcurrentDictionary<int, PacketMerger>();
            _reliableSender = new ReliableSender();
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
            _socket?.Dispose();
            _socket = null;
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

        public IDisposable RegisterCallback(int key, Action<ReadOnlyMemory<byte>> callback)
        {
            if (!_callbacks.ContainsKey(key))
            {
                _callbacks[key] = new List<Action<ReadOnlyMemory<byte>>>();
            }

            _callbacks[key].Add(callback);
            return new CallbackDisposer(this, callback);
        }

        public void UnregisterCallback(Action<ReadOnlyMemory<byte>> callback)
        {
            foreach (int key in _callbacks.Keys)
            {
                _callbacks[key].Remove(callback);
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            byte[] receiveBuffer = new byte[_config.Mtu + 100];
            ArraySegment<byte> receiveSegment = new(receiveBuffer);
            EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await _socket.ReceiveFromAsync(receiveSegment, SocketFlags.None, remoteEndPoint);
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

                    if (_callbacks.TryGetValue(header.Key, out List<Action<ReadOnlyMemory<byte>>> callbacks))
                    {
                        foreach (var callback in callbacks)
                        {
                            callback(owner.Memory[..packetMerger.ReceivedSize]);
                        }
                    }

                    _packetMergers.TryRemove(header.PacketId, out _);
                }

            }
        }

        public void Send(IPEndPoint target, int key, byte[] data, bool isReliable = true)
        {
            Task.Run(() => _reliableSender.SendAsync(_socket, target, key, data, _config, isReliable));
        }

        private void SendAck(IPEndPoint sender, int packetId, int seq)
        {
            byte[] ackPacket = ArrayPool<byte>.Shared.Rent(PacketHeader.Size);
            try
            {
                PacketHelper.SetHeader(ackPacket, new PacketHeader(packetId, (ushort)seq, 0, 0));
                _socket.SendTo(ackPacket, ackPacket.Length, SocketFlags.None, sender);
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
            private readonly Action<ReadOnlyMemory<byte>> _callback;

            public CallbackDisposer(Rudp2pClient parent, Action<ReadOnlyMemory<byte>> callback)
            {
                _parent = parent;
                _callback = callback;
            }

            public void Dispose()
            {
                _parent.UnregisterCallback(_callback);
            }
        }
    }
}
﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rudp2p
{
    /// <summary>
    /// Reliable, unordered, datagram protocol for peer-to-peer communication
    /// </summary>
    public class Rudp2pClient : IDisposable
    {
        /// <summary>
        /// Maximum Transmission Unit (MTU) in bytes
        /// </summary>
        public int Mtu { get; set; } = 2000;
        public UdpClient UdpClient => _udpClient;

        private UdpClient _udpClient;
        private IPEndPoint _remoteEndPoint;
        private int _port;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<int, PacketMerger> _packetMergers;
        private readonly Dictionary<int, List<Action<byte[]>>> _callbacks = new();
        private ReliableSender _reliableSender;
        private SynchronizationContext _originalContext;

        public void Start(int port)
        {
            _port = port;
            _packetMergers = new ConcurrentDictionary<int, PacketMerger>();
            _reliableSender = new ReliableSender();
            _udpClient = new UdpClient(port);
            _originalContext = SynchronizationContext.Current;

            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            Task.Run(() => ReceiveLoop(_cts.Token));
        }

        public void Close()
        {
            _reliableSender?.Dispose();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _udpClient?.Close();
            _udpClient = null;
        }

        public void Dispose()
        {
            Close();
        }

        public IDisposable RegisterCallback(int key, Action<byte[]> callback)
        {
            if (!_callbacks.ContainsKey(key))
            {
                _callbacks[key] = new List<Action<byte[]>>();
            }

            _callbacks[key].Add(callback);
            return new CallbackDisposer(this, callback);
        }

        public void UnregisterCallback(Action<byte[]> callback)
        {
            foreach (int key in _callbacks.Keys)
            {
                _callbacks[key].Remove(callback);
            }
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync();
                OnReceiveData(result.Buffer, result.RemoteEndPoint);
            }
        }

        private void OnReceiveData(byte[] data, IPEndPoint sender)
        {
            var header = Packet.GetHeader(data);

            if (header.TotalSeqNum == 0)
            {
                // Ack Received
                _reliableSender.ReportAck(header.PacketId, header.SeqId);
                return;
            }

            byte[] payload = new byte[data.Length - Packet.HeaderSize];
            Buffer.BlockCopy(data, Packet.HeaderSize, payload, 0, payload.Length);

            SendAck(sender, header.PacketId, header.SeqId);

            if (!_packetMergers.ContainsKey(header.PacketId))
            {
                _packetMergers[header.PacketId] = new PacketMerger(header.TotalSeqNum);
            }

            if (_packetMergers[header.PacketId].AddPacket(header.SeqId, payload))
            {
                byte[] completeData = _packetMergers[header.PacketId].GetMergedData();
                if (_callbacks.TryGetValue(header.Key, out List<Action<byte[]>> callback1))
                {
                    foreach (var callback in callback1)
                    {
                        callback(completeData);
                    }
                }

                _packetMergers.TryRemove(header.PacketId, out _);
            }
        }

        public void Send(IPEndPoint target, int key, byte[] data, bool isReliable = true)
        {
            Task.Run(() => _reliableSender.Send(_udpClient, target, key, data, Mtu, isReliable));
        }

        private void SendAck(IPEndPoint sender, int packetId, int seq)
        {
            byte[] ackPacket = new byte[Packet.HeaderSize];
            Packet.SetHeader(ref ackPacket, packetId, (ushort)seq, 0, 0);
            _udpClient.Send(ackPacket, ackPacket.Length, sender);
        }

        public class CallbackDisposer : IDisposable
        {
            private readonly Rudp2pClient _parent;
            private readonly Action<byte[]> _callback;

            public CallbackDisposer(Rudp2pClient parent, Action<byte[]> callback)
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
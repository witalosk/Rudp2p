using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rudp2p.Tests
{
    public class Rudp2pClientTests
    {
        private static int _portCounter = 48100;
        private readonly List<IDisposable> _disposables = new();

        private static int NextPort() => Interlocked.Increment(ref _portCounter);

        private static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);

        private Rudp2pClient CreateClient(int port, Rudp2pConfig config = null)
        {
            var client = config == null ? new Rudp2pClient() : new Rudp2pClient(config);
            _disposables.Add(client);
            client.Start(port);
            return client;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
            _disposables.Clear();
        }

        private static async Task<T> WaitAsync<T>(Task<T> task, int timeoutMs = 5000)
        {
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task)
            {
                Assert.Fail($"Timed out after {timeoutMs} ms waiting for received data");
            }
            return await task;
        }

        private static TaskCompletionSource<byte[]> RegisterReceiveOnce(Rudp2pClient client, int key)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.RegisterCallback(key, data => tcs.TrySetResult(data.Data.ToArray()));
            return tcs;
        }

        [Test]
        public async Task ReliableSend_SmallPayload_IsReceived()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            var received = RegisterReceiveOnce(receiver, key: 7);
            byte[] payload = { 1, 2, 3, 4, 5 };

            await sender.SendAsync(Loopback(portB), 7, payload);

            CollectionAssert.AreEqual(payload, await WaitAsync(received.Task));
        }

        [Test]
        public async Task ReliableSend_MultiFragmentPayload_IsReassembled()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            var received = RegisterReceiveOnce(receiver, key: 0);

            // ~150 fragments with the default MTU, which also exercises the send window
            byte[] payload = new byte[200_000];
            new Random(42).NextBytes(payload);

            await sender.SendAsync(Loopback(portB), 0, payload);

            byte[] result = await WaitAsync(received.Task, 10000);
            Assert.AreEqual(payload.Length, result.Length);
            CollectionAssert.AreEqual(payload, result);
        }

        [Test]
        public async Task UnreliableSend_IsReceived()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            var received = RegisterReceiveOnce(receiver, key: 1);
            byte[] payload = { 10, 20, 30 };

            await sender.SendAsync(Loopback(portB), 1, payload, isReliable: false);

            CollectionAssert.AreEqual(payload, await WaitAsync(received.Task));
        }

        [Test]
        public async Task Callback_OnlyReceivesItsOwnKey()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            var keyOne = RegisterReceiveOnce(receiver, key: 1);
            var keyTwo = RegisterReceiveOnce(receiver, key: 2);

            await sender.SendAsync(Loopback(portB), 2, new byte[] { 42 });

            CollectionAssert.AreEqual(new byte[] { 42 }, await WaitAsync(keyTwo.Task));
            Assert.IsFalse(keyOne.Task.IsCompleted, "Callback for a different key must not fire");
        }

        [Test]
        public async Task UnregisteredCallback_DoesNotFire()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            bool fired = false;
            IDisposable registration = receiver.RegisterCallback(0, _ => fired = true);
            registration.Dispose();

            var stillListening = RegisterReceiveOnce(receiver, key: 0);
            await sender.SendAsync(Loopback(portB), 0, new byte[] { 1 });

            // The remaining callback proves the message arrived while the disposed one stayed silent
            await WaitAsync(stillListening.Task);
            Assert.IsFalse(fired, "Disposed callback must not fire");
        }

        [Test]
        public async Task ReceiveData_RemoteEndPoint_IsSenderEndpoint()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.RegisterCallback(0, data => tcs.TrySetResult(data.RemoteEndPoint));

            await sender.SendAsync(Loopback(portB), 0, new byte[] { 1 });

            IPEndPoint remote = await WaitAsync(tcs.Task);
            Assert.AreEqual(portA, remote.Port);
        }

        [Test]
        public async Task SendAsync_BeforeStart_ThrowsInvalidOperation()
        {
            var client = new Rudp2pClient();
            _disposables.Add(client);

            try
            {
                await client.SendAsync(Loopback(NextPort()), 0, new byte[] { 1 });
                Assert.Fail("Expected InvalidOperationException");
            }
            catch (InvalidOperationException)
            {
            }
        }

        [Test]
        public async Task ReliableSend_WithoutAck_ThrowsRudp2pSendException()
        {
            int portA = NextPort(), portB = NextPort();
            var config = new Rudp2pConfig { ReliableRetryCount = 2, ReliableRetryInterval = 30 };
            var sender = CreateClient(portA, config);

            // A raw UDP socket that receives but never replies with ACKs
            var silentReceiver = new UdpClient(portB);
            _disposables.Add(silentReceiver);

            try
            {
                await sender.SendAsync(Loopback(portB), 0, new byte[] { 1, 2, 3 });
                Assert.Fail("Expected Rudp2pSendException");
            }
            catch (Rudp2pSendException)
            {
            }
        }

        [Test]
        public async Task SendAsync_CanBeCancelled()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);

            var silentReceiver = new UdpClient(portB);
            _disposables.Add(silentReceiver);

            using var cts = new CancellationTokenSource(50);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await sender.SendAsync(Loopback(portB), 0, new byte[] { 1 }, cancellationToken: cts.Token);
                Assert.Fail("Expected OperationCanceledException");
            }
            catch (OperationCanceledException)
            {
            }

            // Default retries would take well over a second; cancellation must cut that short
            Assert.Less(stopwatch.ElapsedMilliseconds, 1500);
        }

        [Test]
        public async Task Restart_OnSamePort_StillReceives()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            receiver.Close();
            receiver.Start(portB);

            var received = RegisterReceiveOnce(receiver, key: 0);
            await sender.SendAsync(Loopback(portB), 0, new byte[] { 9 });

            CollectionAssert.AreEqual(new byte[] { 9 }, await WaitAsync(received.Task));
        }

        [Test]
        public async Task DuplicateMessage_IsDeliveredOnlyOnce()
        {
            int portA = NextPort(), portB = NextPort();
            var sender = CreateClient(portA);
            var receiver = CreateClient(portB);

            int deliveryCount = 0;
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.RegisterCallback(0, _ =>
            {
                Interlocked.Increment(ref deliveryCount);
                first.TrySetResult(true);
            });

            await sender.SendAsync(Loopback(portB), 0, new byte[] { 1, 2, 3 });
            await WaitAsync(first.Task);

            // Give any duplicate deliveries (e.g. from retransmits) time to surface
            await Task.Delay(300);
            Assert.AreEqual(1, deliveryCount);
        }

        [Test]
        public async Task NonRudp2pDatagram_IsIgnored()
        {
            int portA = NextPort(), portB = NextPort();
            var receiver = CreateClient(portB);
            var received = RegisterReceiveOnce(receiver, key: 0);

            // Send garbage that is long enough to be parsed as a header
            using var rawSender = new UdpClient(portA);
            byte[] garbage = new byte[64];
            new Random(7).NextBytes(garbage);
            await rawSender.SendAsync(garbage, garbage.Length, Loopback(portB));

            await Task.Delay(300);
            Assert.IsFalse(received.Task.IsCompleted, "Garbage datagram must not reach callbacks");

            // The receive loop must still be alive afterwards
            var realSender = CreateClient(NextPort());
            await realSender.SendAsync(Loopback(portB), 0, new byte[] { 5 });
            CollectionAssert.AreEqual(new byte[] { 5 }, await WaitAsync(received.Task));
        }
    }
}

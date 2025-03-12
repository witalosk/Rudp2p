using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rudp2p
{
    internal class SendQueue : IDisposable
    {
        private readonly ConcurrentQueue<(UdpClient Client, byte[] Data, int DataLength, IPEndPoint EndPoint, TaskCompletionSource<bool> Tcs)> _queue = new();
        private readonly TokenBucket _tokenBucket;
        private readonly CancellationTokenSource _cts;

        public SendQueue(int bucketSize, int refillRate)
        {
            _tokenBucket = new TokenBucket(bucketSize, refillRate);
            _cts = new CancellationTokenSource();

            Task.Run(ProcessQueue);
        }

        public Task Enqueue(UdpClient client, IPEndPoint target, byte[] data, int dataLength)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue((client, data, dataLength, target, tcs));
            return tcs.Task;
        }

        private async Task ProcessQueue()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var item))
                {
                    while (!_tokenBucket.TryConsume(item.Data.Length))
                    {
                        // Wait until the token is available
                        await Task.Delay(10);
                    }

                    try
                    {
                        await item.Client.SendAsync(item.Data, item.DataLength, item.EndPoint);
                        item.Tcs.SetResult(true);
                    }
                    catch (Exception e)
                    {
                        item.Tcs.SetException(e);
                    }
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _tokenBucket?.Dispose();
        }
    }
}
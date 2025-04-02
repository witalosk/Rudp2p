using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Rudp2p
{
    internal class SendQueue : IDisposable
    {
        private readonly ConcurrentQueue<(Socket Client, ArraySegment<byte> Data, IPEndPoint EndPoint, TaskCompletionSource<bool> Tcs)> _queue = new();
        private readonly TokenBucket _tokenBucket;
        private readonly CancellationTokenSource _cts;

        public SendQueue(int bucketSize, int refillRate)
        {
            _tokenBucket = new TokenBucket(bucketSize, refillRate);
            _cts = new CancellationTokenSource();

            Task.Run(ProcessQueue);
        }

        public Task Enqueue(Socket client, IPEndPoint target, ArraySegment<byte> data)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue((client, data, target, tcs));
            return tcs.Task;
        }

        private async Task ProcessQueue()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var item))
                {
                    while (!_tokenBucket.TryConsume(item.Data.Count))
                    {
                        // Wait until the token is available
                        await Task.Delay(1);
                    }

                    try
                    {
                        await item.Client.SendToAsync(item.Data, SocketFlags.None, item.EndPoint);
                        item.Tcs.SetResult(true);
                    }
                    catch (Exception e)
                    {
                        item.Tcs.SetException(e);
                    }
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
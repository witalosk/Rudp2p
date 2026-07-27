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
        private readonly ConcurrentQueue<(Socket Client, ArraySegment<byte> Data, IPEndPoint EndPoint, TaskCompletionSource<bool> Tcs)> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
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
            if (_cts.IsCancellationRequested)
            {
                tcs.TrySetCanceled();
                return tcs.Task;
            }

            _queue.Enqueue((client, data, target, tcs));
            _signal.Release();
            return tcs.Task;
        }

        private async Task ProcessQueue()
        {
            CancellationToken token = _cts.Token;

            try
            {
                while (true)
                {
                    // Blocks until an item is enqueued instead of busy-spinning on TryDequeue
                    await _signal.WaitAsync(token);
                    if (!_queue.TryDequeue(out var item)) continue;

                    try
                    {
                        while (!_tokenBucket.TryConsume(item.Data.Count, out int waitMs))
                        {
                            await Task.Delay(waitMs, token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        item.Tcs.TrySetCanceled();
                        throw;
                    }

                    try
                    {
                        await item.Client.SendToAsync(item.Data, SocketFlags.None, item.EndPoint);
                        item.Tcs.TrySetResult(true);
                    }
                    catch (Exception e)
                    {
                        item.Tcs.TrySetException(e);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                DrainPendingItems();
            }
        }

        private void DrainPendingItems()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Tcs.TrySetCanceled();
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            DrainPendingItems();
            _cts?.Dispose();
            _tokenBucket?.Dispose();
        }
    }
}

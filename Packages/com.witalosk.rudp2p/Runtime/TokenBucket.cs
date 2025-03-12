using System;
using System.Threading;
using System.Threading.Tasks;

namespace Rudp2p
{
    internal class TokenBucket : IDisposable
    {
        private readonly int _bucketSize;
        private readonly int _refillRate;
        private int _tokens;
        private CancellationTokenSource _cts;
        private readonly object _lockObj = new();

        public TokenBucket(int bucketSize, int refillRatePerSec)
        {
            _bucketSize = bucketSize;
            _refillRate = refillRatePerSec / 10;
            _tokens = bucketSize;
            _cts = new CancellationTokenSource();

            Task.Run(RefillTokens);
        }
        
        private void RefillTokens()
        {
            while (!_cts.IsCancellationRequested)
            {
                lock (_lockObj)
                {
                    _tokens = Math.Min(_tokens + _refillRate, _bucketSize);
                }
                
                // Add tokens every 100ms
                Thread.Sleep(100);
            }
        }

        public bool TryConsume(int amount)
        {
            lock (_lockObj)
            {
                if (_tokens >= amount)
                {
                    _tokens -= amount;
                    return true;
                }
                return false;
            }
        }
        
        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }
}
using System;
using System.Diagnostics;

namespace Rudp2p
{
    internal class TokenBucket : IDisposable
    {
        private readonly int _bucketSize;
        private readonly double _refillRatePerSec;
        private double _tokens;
        private long _lastRefillTimestamp;
        private readonly object _lockObj = new();

        public TokenBucket(int bucketSize, int refillRatePerSec)
        {
            _bucketSize = bucketSize;
            _refillRatePerSec = refillRatePerSec;
            _tokens = bucketSize;
            _lastRefillTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Tries to consume tokens, refilling lazily based on elapsed time (no background thread).
        /// On failure, waitMs is the estimated time until enough tokens accumulate.
        /// </summary>
        public bool TryConsume(int amount, out int waitMs)
        {
            lock (_lockObj)
            {
                Refill();

                // Requests larger than the bucket are allowed once the bucket is full,
                // letting the balance go negative so they still throttle subsequent sends
                double required = Math.Min(amount, _bucketSize);
                if (_tokens >= required)
                {
                    _tokens -= amount;
                    waitMs = 0;
                    return true;
                }

                waitMs = Math.Max(1, (int)Math.Ceiling((required - _tokens) * 1000.0 / _refillRatePerSec));
                return false;
            }
        }

        private void Refill()
        {
            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
            _lastRefillTimestamp = now;
            _tokens = Math.Min(_tokens + elapsedSec * _refillRatePerSec, _bucketSize);
        }

        public void Dispose()
        {
        }
    }
}

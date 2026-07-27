using NUnit.Framework;
using System.Threading;

namespace Rudp2p.Tests
{
    public class TokenBucketTests
    {
        [Test]
        public void TryConsume_SucceedsWhileTokensRemain()
        {
            using var bucket = new TokenBucket(1000, 1);

            Assert.IsTrue(bucket.TryConsume(400, out _));
            Assert.IsTrue(bucket.TryConsume(400, out _));
        }

        [Test]
        public void TryConsume_FailsWhenDepleted_AndReportsWaitTime()
        {
            using var bucket = new TokenBucket(1000, 1000);

            Assert.IsTrue(bucket.TryConsume(1000, out _));
            Assert.IsFalse(bucket.TryConsume(500, out int waitMs));
            Assert.Greater(waitMs, 0);
        }

        [Test]
        public void TryConsume_RefillsOverTime()
        {
            using var bucket = new TokenBucket(1000, 100000); // Refills the whole bucket in 10ms

            Assert.IsTrue(bucket.TryConsume(1000, out _));
            Assert.IsFalse(bucket.TryConsume(1000, out _));

            Thread.Sleep(100); // Plenty of time for a full refill

            Assert.IsTrue(bucket.TryConsume(1000, out _));
        }

        [Test]
        public void TryConsume_AllowsRequestsLargerThanBucketWhenFull()
        {
            using var bucket = new TokenBucket(1000, 1000);

            // An oversized request must not dead-lock forever: it is allowed when the
            // bucket is full and drives the balance negative to throttle later sends
            Assert.IsTrue(bucket.TryConsume(5000, out _));
            Assert.IsFalse(bucket.TryConsume(100, out int waitMs));
            Assert.Greater(waitMs, 0);
        }
    }
}

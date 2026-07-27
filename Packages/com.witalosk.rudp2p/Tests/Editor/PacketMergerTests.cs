using NUnit.Framework;
using System;

namespace Rudp2p.Tests
{
    public class PacketMergerTests
    {
        [Test]
        public void AddPacket_ReturnsTrueOnlyWhenAllFragmentsArrive()
        {
            using var merger = new PacketMerger(3);

            Assert.IsFalse(merger.AddPacket(0, new byte[] { 1 }));
            Assert.IsFalse(merger.AddPacket(1, new byte[] { 2 }));
            Assert.IsTrue(merger.AddPacket(2, new byte[] { 3 }));
        }

        [Test]
        public void SetMergedData_ReassemblesOutOfOrderFragments()
        {
            using var merger = new PacketMerger(3);

            merger.AddPacket(2, new byte[] { 5, 6 });
            merger.AddPacket(0, new byte[] { 1, 2 });
            merger.AddPacket(1, new byte[] { 3, 4 });

            Assert.AreEqual(6, merger.ReceivedSize);

            byte[] merged = new byte[merger.ReceivedSize];
            merger.SetMergedData(merged);

            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, merged);
        }

        [Test]
        public void AddPacket_IgnoresDuplicateFragment()
        {
            using var merger = new PacketMerger(2);

            Assert.IsFalse(merger.AddPacket(0, new byte[] { 1 }));

            // A duplicate must neither complete the message nor inflate the received count
            Assert.IsFalse(merger.AddPacket(0, new byte[] { 9 }));
            Assert.AreEqual(1, merger.ReceivedSize);

            Assert.IsTrue(merger.AddPacket(1, new byte[] { 2 }));
        }

        [Test]
        public void AddPacket_RejectsOutOfRangeSequenceNumbers()
        {
            using var merger = new PacketMerger(2);

            Assert.IsFalse(merger.AddPacket(-1, new byte[] { 1 }));
            Assert.IsFalse(merger.AddPacket(2, new byte[] { 1 }));
            Assert.AreEqual(0, merger.ReceivedSize);
        }

        [Test]
        public void AddPacket_ReturnsFalseAfterDispose()
        {
            var merger = new PacketMerger(2);
            merger.Dispose();

            Assert.IsFalse(merger.AddPacket(0, new byte[] { 1 }));
        }

        [Test]
        public void LastReceivedUtc_UpdatesWhenFragmentArrives()
        {
            using var merger = new PacketMerger(2);
            DateTime before = merger.LastReceivedUtc;

            System.Threading.Thread.Sleep(20);
            merger.AddPacket(0, new byte[] { 1 });

            Assert.Greater(merger.LastReceivedUtc, before);
        }
    }
}

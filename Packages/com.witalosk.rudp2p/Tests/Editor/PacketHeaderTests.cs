using NUnit.Framework;
using System;

namespace Rudp2p.Tests
{
    public class PacketHeaderTests
    {
        [TestCase((byte)PacketType.Data)]
        [TestCase((byte)PacketType.Ack)]
        [TestCase((byte)PacketType.UnreliableData)]
        public void SetHeader_TryGetHeader_Roundtrip(byte typeValue)
        {
            var type = (PacketType)typeValue;
            var original = new PacketHeader(type, 12345678, 42, 100, -99);
            byte[] buffer = new byte[PacketHeader.Size];

            PacketHelper.SetHeader(buffer, original);

            Assert.IsTrue(PacketHelper.TryGetHeader(buffer, out var parsed));
            Assert.AreEqual(original.Type, parsed.Type);
            Assert.AreEqual(original.PacketId, parsed.PacketId);
            Assert.AreEqual(original.SeqId, parsed.SeqId);
            Assert.AreEqual(original.TotalSeqNum, parsed.TotalSeqNum);
            Assert.AreEqual(original.Key, parsed.Key);
        }

        [Test]
        public void TryGetHeader_RejectsTooShortData()
        {
            byte[] buffer = new byte[PacketHeader.Size - 1];
            Assert.IsFalse(PacketHelper.TryGetHeader(buffer, out _));
        }

        [Test]
        public void TryGetHeader_RejectsWrongMagicNumber()
        {
            byte[] buffer = new byte[PacketHeader.Size];
            PacketHelper.SetHeader(buffer, new PacketHeader(PacketType.Data, 1, 0, 1, 0));
            buffer[0] ^= 0xFF; // Corrupt the magic number

            Assert.IsFalse(PacketHelper.TryGetHeader(buffer, out _));
        }

        [Test]
        public void TryGetHeader_RejectsUnknownPacketType()
        {
            byte[] buffer = new byte[PacketHeader.Size];
            PacketHelper.SetHeader(buffer, new PacketHeader(PacketType.Data, 1, 0, 1, 0));
            buffer[2] = 99; // Unknown type

            Assert.IsFalse(PacketHelper.TryGetHeader(buffer, out _));
        }

        [Test]
        public void TryGetHeader_RejectsRandomGarbage()
        {
            var random = new Random(1234);
            byte[] buffer = new byte[64];

            for (int i = 0; i < 1000; i++)
            {
                random.NextBytes(buffer);
                if (PacketHelper.TryGetHeader(buffer, out _))
                {
                    // Only passes if the garbage happens to contain both the magic number
                    // and a valid type byte, which is astronomically unlikely in 1000 tries
                    Assert.Fail($"Garbage datagram was accepted at iteration {i}");
                }
            }
        }

        [Test]
        public void GetPayload_ReturnsDataAfterHeader()
        {
            byte[] buffer = new byte[PacketHeader.Size + 3];
            buffer[PacketHeader.Size] = 0xAA;
            buffer[PacketHeader.Size + 1] = 0xBB;
            buffer[PacketHeader.Size + 2] = 0xCC;

            var payload = PacketHelper.GetPayload(buffer);

            Assert.AreEqual(3, payload.Length);
            Assert.AreEqual(0xAA, payload.Span[0]);
            Assert.AreEqual(0xCC, payload.Span[2]);
        }

        [Test]
        public void GetPayload_ReturnsEmptyForHeaderOnlyPacket()
        {
            byte[] buffer = new byte[PacketHeader.Size];
            Assert.AreEqual(0, PacketHelper.GetPayload(buffer).Length);
        }
    }
}

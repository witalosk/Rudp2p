using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rudp2p
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct PacketHeader
    {
        public int PacketId;
        public ushort SeqId;
        public ushort TotalSeqNum;
        public int Key;

        public const int Size = sizeof(int) * 2 + sizeof(ushort) * 2;

        public PacketHeader(int packetId, ushort seqId, ushort totalSeqNum, int key)
        {
            PacketId = packetId;
            SeqId = seqId;
            TotalSeqNum = totalSeqNum;
            Key = key;
        }

        public override string ToString()
        {
            return $"PacketId: {PacketId}, SeqId: {SeqId}, TotalSeqNum: {TotalSeqNum}, Key: {Key}";
        }
    }

    internal static class PacketHelper
    {
        public static void SetHeader(Span<byte> buffer, PacketHeader header)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, header.PacketId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], header.SeqId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], header.TotalSeqNum);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[8..], header.Key);
        }

        public static PacketHeader GetHeader(ReadOnlySpan<byte> data)
        {
            return new PacketHeader
            (
                BinaryPrimitives.ReadInt32LittleEndian(data),
                BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[8..])
            );
        }

        public static ReadOnlyMemory<byte> GetPayload(ReadOnlyMemory<byte> packetData)
        {
            return packetData.Length <= PacketHeader.Size
                ? ReadOnlyMemory<byte>.Empty
                : packetData[PacketHeader.Size..];
        }
    }
}
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rudp2p
{
    internal enum PacketType : byte
    {
        /// <summary>Reliable data fragment; the receiver must reply with an Ack</summary>
        Data = 1,
        Ack = 2,
        /// <summary>Unreliable data fragment; no Ack is expected, saving return bandwidth</summary>
        UnreliableData = 3,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct PacketHeader
    {
        public PacketType Type;
        public int PacketId;
        public ushort SeqId;
        public ushort TotalSeqNum;
        public int Key;

        public const ushort MagicNumber = 0x5250;
        public const int Size = sizeof(ushort) + sizeof(byte) + sizeof(int) * 2 + sizeof(ushort) * 2;

        public PacketHeader(PacketType type, int packetId, ushort seqId, ushort totalSeqNum, int key)
        {
            Type = type;
            PacketId = packetId;
            SeqId = seqId;
            TotalSeqNum = totalSeqNum;
            Key = key;
        }

        public override string ToString()
        {
            return $"Type: {Type}, PacketId: {PacketId}, SeqId: {SeqId}, TotalSeqNum: {TotalSeqNum}, Key: {Key}";
        }
    }

    internal static class PacketHelper
    {
        public static void SetHeader(Span<byte> buffer, PacketHeader header)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, PacketHeader.MagicNumber);
            buffer[2] = (byte)header.Type;
            BinaryPrimitives.WriteInt32LittleEndian(buffer[3..], header.PacketId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[7..], header.SeqId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer[9..], header.TotalSeqNum);
            BinaryPrimitives.WriteInt32LittleEndian(buffer[11..], header.Key);
        }

        /// <summary>
        /// Parses and validates a header. Returns false for datagrams that are too short,
        /// have a wrong magic number, or an unknown packet type.
        /// </summary>
        public static bool TryGetHeader(ReadOnlySpan<byte> data, out PacketHeader header)
        {
            header = default;

            if (data.Length < PacketHeader.Size) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(data) != PacketHeader.MagicNumber) return false;

            byte type = data[2];
            if (type != (byte)PacketType.Data && type != (byte)PacketType.Ack && type != (byte)PacketType.UnreliableData) return false;

            header = new PacketHeader
            (
                (PacketType)type,
                BinaryPrimitives.ReadInt32LittleEndian(data[3..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[7..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[9..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[11..])
            );
            return true;
        }

        public static ReadOnlyMemory<byte> GetPayload(ReadOnlyMemory<byte> packetData)
        {
            return packetData.Length <= PacketHeader.Size
                ? ReadOnlyMemory<byte>.Empty
                : packetData[PacketHeader.Size..];
        }
    }
}

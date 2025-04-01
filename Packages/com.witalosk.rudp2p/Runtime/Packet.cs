using System;

namespace Rudp2p
{
    internal class Packet
    {
        public int PacketId { get; }
        public int SendIndex { get; }
        public ushort SeqId { get; }
        public ushort TotalSeqNum { get; }
        public int Key { get; }

        public Packet(int packetId, int sendIndex, ushort seqId, ushort totalSeqNum, int key)
        {
            PacketId = packetId;
            SendIndex = sendIndex;
            SeqId = seqId;
            TotalSeqNum = totalSeqNum;
            Key = key;
        }

        public const int HeaderSize = 16;

        public static void SetHeader(ref byte[] target, int packetId, int sendIndex, ushort seqId, ushort totalSeqNum, int key)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(packetId), 0, target, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(sendIndex), 0, target, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seqId), 0, target, 8, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(totalSeqNum), 0, target, 10, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(key), 0, target, 12, 4);
        }
        
        public static Packet GetHeader(in byte[] data)
        {
            int packetId = BitConverter.ToInt32(data, 0);
            int sendIndex = BitConverter.ToInt32(data, 4);
            ushort seqId = BitConverter.ToUInt16(data, 8);
            ushort totalSeqNum = BitConverter.ToUInt16(data, 10);
            int key = BitConverter.ToInt32(data, 12);
            return new Packet(packetId, sendIndex, seqId, totalSeqNum, key);
        }

        public override string ToString()
        {
            return $"PacketId: {PacketId}, SendIndex: {SendIndex}, SeqId: {SeqId}, TotalSeqNum: {TotalSeqNum}, Key: {Key}";
        }
    }
}
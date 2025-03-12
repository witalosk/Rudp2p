using System;

namespace Rudp2p
{
    internal class Packet
    {
        public int PacketId { get; }
        public ushort SeqId { get; }
        public ushort TotalSeqNum { get; }
        public int Key { get; }
        
        public Packet(int packetId, ushort seqId, ushort totalSeqNum, int key)
        {
            PacketId = packetId;
            SeqId = seqId;
            TotalSeqNum = totalSeqNum;
            Key = key;
        }
        
        public const int HeaderSize = 12;
        
        public static void SetHeader(ref byte[] target, int packetId, ushort seqId, ushort totalSeqNum, int key)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(packetId), 0, target, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(seqId), 0, target, 4, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(totalSeqNum), 0, target, 6, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(key), 0, target, 8, 4);
        }
        
        public static Packet GetHeader(in byte[] data)
        {
            int packetId = BitConverter.ToInt32(data, 0);
            ushort seqId = BitConverter.ToUInt16(data, 4);
            ushort totalSeqNum = BitConverter.ToUInt16(data, 6);
            int key = BitConverter.ToInt32(data, 8);
            return new Packet(packetId, seqId, totalSeqNum, key);
        }

        public override string ToString()
        {
            return $"PacketId: {PacketId}, SeqId: {SeqId}, TotalSeqNum: {TotalSeqNum}, Key: {Key}";
        }
    }
}
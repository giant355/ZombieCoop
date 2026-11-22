using Google.Protobuf;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

public static class MessageCodec
{
    public const int MaxMessageLength = 64 * 1024;

    /// <summary>编码消息；长度包含两字节类型编号和 Protobuf 正文</summary>
    public static byte[] EncodeTcpMessage(MessageType messageType, IMessage message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));

        byte[] payload = message.ToByteArray();
        if (payload.Length > MaxMessageLength - 2) throw new ArgumentException("消息过大。", nameof(message));

        int length = 2 + payload.Length;
        byte[] packet = new byte[4 + length];
        byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length));

        Buffer.BlockCopy(header, 0, packet, 0, 4);
        ushort typeId = (ushort)messageType;
        packet[4] = (byte)(typeId >> 8);
        packet[5] = (byte)(typeId & 0xFF);
        Buffer.BlockCopy(payload, 0, packet, 6, payload.Length);
        return packet;
    }

    /// <summary>编码消息；相比于tcp的，不需要长度信息</summary>
    public static byte[] EncodeUdpMessage(MessageType messageType, IMessage message)
    {
        byte[] payload = message.ToByteArray();
        byte[] packet = new byte[2 + payload.Length];
        ushort typeId = (ushort)messageType;
        packet[0] = (byte)(typeId >> 8);
        packet[1] = (byte)(typeId & 0xFF);
        Buffer.BlockCopy(payload, 0, packet, 2, payload.Length);
        return packet;
    }

    /// <summary>阻塞接收，直到填满数组；对端提前关闭时抛出异常。</summary>
    public static void ReceiveExactly(Socket socket, byte[] buffer)
    {
        if (socket == null) throw new ArgumentNullException(nameof(socket));
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));

        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = socket.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
            if (count == 0) throw new IOException("对端已关闭连接，无法继续接收。");
            offset += count;
        }
    }
}
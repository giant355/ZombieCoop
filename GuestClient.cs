using Google.Protobuf;
using HostAuthorityDemo.Protocol;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class GuestClient : Client
{
    private Socket socket;
    private ConcurrentQueue<byte[]> sendQueue = new ConcurrentQueue<byte[]>();
    private ConcurrentQueue<IMessage> receiveQueue = new ConcurrentQueue<IMessage>();



    /// <summary> 用于在发送操作之间进行线程同步的自动重置信号，减少发送线程空转导致的性能消耗 </summary>
    private AutoResetEvent sendSignal = new AutoResetEvent(false);
    private Thread sendThread;
    private Thread receiveThread;



    private volatile bool connected;
    private volatile bool stopping;



    [SerializeField] private string serverIp = "127.0.0.1";
    [SerializeField] private int serverPort = 5050;



    //消息类型&解析器注册表
    private readonly Dictionary<MessageType, MessageParser> messageTypeRegistry = new Dictionary<MessageType, MessageParser>();
    //消息处理器注册表
    private readonly Dictionary<Type, Action<IMessage>> handlersRegistry = new Dictionary<Type, Action<IMessage>>();
    
    //-----------------------------------------------------------------------开始相关
    void Start()
    {
        //为类型解析器注册表添加数据
        messageTypeRegistry.Add(MessageType.move_Input, PlayerMove.Parser);

        //为消息处理器注册表添加数据
        handlersRegistry.Add(typeof(PlayerMove), (message) => { Handle_WorldSnapshot((WorldSnapshot)message);});

        socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.BeginConnect(IPAddress.Parse(serverIp), serverPort, OnConnected, null);
        }
        catch (Exception error)
        {
            Debug.LogError("发起连接失败：" + error.Message);
            socket.Close();
        }
    }
    private void OnConnected(IAsyncResult result)
    {
        try
        {
            socket.EndConnect(result);
            if (stopping) return;
            sendThread = new Thread(SendLoop) { IsBackground = true };
            sendThread.Start();

            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();

            connected = true;
            Debug.Log("已连接房主");
            // 下一步：开始接收房主消息。
        }
        catch (ObjectDisposedException) { } // 退出游戏时可能已经关闭。
        catch (SocketException error)
        {
            Debug.LogError("连接失败：" + error.SocketErrorCode);
            socket.Close();
        }
    }
    //-----------------------------------------------------------------------发送循环和接收循环
    private void SendLoop()
    {
        try
        {
            while (!stopping)
            {
                if (!sendQueue.TryDequeue(out byte[] packet))
                {
                    sendSignal.WaitOne(); // 没消息，阻塞当前发送线程。
                    continue;
                }

                int offset = 0;
                while (!stopping && offset < packet.Length)
                {
                    int sent = socket.Send(packet, offset, packet.Length - offset, SocketFlags.None);
                    if (sent == 0) throw new System.IO.IOException("无法继续发送。");
                    offset += sent;
                }
            }
        }
        catch (Exception error)
        {
            if (!stopping) print("发送失败：" + error.Message);
            StopSending();
        }
    }
    /// <summary>
    /// 从socket里面取出字节解析为IMessage，放入receiveQueue
    /// </summary>
    private void ReceiveLoop()
    {
        try
        {
            while (!stopping)
            {
                // 长度包含两字节类型编号和正文，不包含长度字段自身
                byte[] header = new byte[4];
                MessageCodec.ReceiveExactly(socket, header);
                int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
                if (length < 2 || length > MessageCodec.MaxMessageLength) throw new System.IO.IOException("消息长度不合法");

                byte[] body = new byte[length];
                MessageCodec.ReceiveExactly(socket, body);
                if (stopping) break;

                MessageType messageType = (MessageType)((body[0] << 8) | body[1]);
                // 按类型编号寻找解析器；未注册的类型跳过
                if (messageTypeRegistry.TryGetValue(messageType, out MessageParser parser))
                {
                    // 使用找到的解析器，跳过两字节类型编号，解析剩余正文
                    IMessage message = parser.ParseFrom(body, 2, body.Length - 2);
                    // 交给主线程处理
                    receiveQueue.Enqueue(message);
                }
            }
        }
        catch (Exception error)
        {
            if (!stopping) Debug.LogWarning("接收失败：" + error.Message);
        }
        finally
        {
            StopSending(); // 关闭 Socket、清理连接标志并唤醒发送线程。
        }
    }
    //-----------------------------------------------------------------------其他生命周期函数

    protected override void Update()
    {
        base.Update();



        while(receiveQueue.TryDequeue(out IMessage message))
        {
            HandleMessage(message);
        }

        sendSignal.Set();
    }

    private void OnDestroy() 
    {
        StopSending();
    }
    //-----------------------------------------------------------------------消息处理及其辅助函数
    private void HandleMessage(IMessage message)
    {
        Type type = message.GetType();

        if (handlersRegistry.TryGetValue(type, out Action<IMessage> handler))
        {
            handler(message);
        }
    }
    private void Handle_WorldSnapshot(WorldSnapshot snapshot)
    {
    }
    //-----------------------------------------------------------------------杂项函数
    /// <summary>
    /// 该方法自动被Update调用
    /// </summary>
    protected override void SendMoveInput_And_AimAngle()
    {
        if (!connected || stopping) return;
        PlayerMove playerMove = new PlayerMove { InputX = MoveInput.x,InputZ = MoveInput.y };
        byte[] moveInput_bytes = MessageCodec.EncodeMessage(MessageType.move_Input, playerMove);
        sendQueue.Enqueue(moveInput_bytes);
    }

    protected override void SendSomeRequests()
    {

    }

    private void StopSending()
    {
        stopping = true;
        connected = false;
        sendSignal.Set(); // 唤醒可能正在等待的线程，让它检查退出标志。
        socket?.Close();  // 解除可能正在阻塞的 Send。
    }
}



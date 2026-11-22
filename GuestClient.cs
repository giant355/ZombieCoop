using Google.Protobuf;
using HostAuthorityDemo.Protocol;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.Windows;

public class GuestClient : Client
{
    private int localPlayerId = -1;


    /// <summary>
    /// 临时使用cube，之后会换成FPS controller rig
    /// </summary>
    /// TODO
    private readonly Dictionary<int,GameObject> players = new Dictionary<int,GameObject>();
    
    
    [SerializeField] private SceneSwitcher sceneSwitcher;
    private bool startSceneRequested;
    private bool localSceneReady;


    private Socket tcpSocket;
    private Socket udpSocket;
    private ConcurrentQueue<byte[]> sendQueue = new ConcurrentQueue<byte[]>();
    private ConcurrentQueue<IMessage> receiveQueue = new ConcurrentQueue<IMessage>();



    /// <summary> 用于在发送操作之间进行线程同步的自动重置信号，减少发送线程空转导致的性能消耗 </summary>
    private AutoResetEvent sendSignal = new AutoResetEvent(false);
    private Thread tcpSendThread;
    private Thread tcpReceiveThread;
    private Thread udpReceiveThread;


    private volatile bool connected;
    private volatile bool stopping;

    public bool HasJoinedRoom => connected && !stopping && localPlayerId > 0;
    public bool ConnectionStopped => stopping;
    public int AssignedPlayerId => localPlayerId;


    [SerializeField] private string serverIp = "127.0.0.1";
    [SerializeField] private int serverPort = 5050;


    InterpolationClock clock = new InterpolationClock();
    SnapshotBuffer<WorldSnapshot> snapshotBuffer = new SnapshotBuffer<WorldSnapshot>();


    private double lastReconciledTimeStamp;


    private ulong? latestRosterTick;
    private readonly List<int> departedPlayers = new List<int>(4);


    //消息类型&解析器注册表
    private readonly Dictionary<MessageType, MessageParser> messageTypeRegistry = new Dictionary<MessageType, MessageParser>();
    //消息处理器注册表
    private readonly Dictionary<Type, Action<IMessage>> handlersRegistry = new Dictionary<Type, Action<IMessage>>();


    //历史需要记录当时输入了什么（方向，在奔跑或跳跃吗），和帧间隔时间
    private readonly SortedDictionary<double,(PlayerMove,float)> prediction_history = new SortedDictionary<double,(PlayerMove,float)>();


    private bool sendingUdpInput;

    //-----------------------------------------------------------------------开始相关
    public void ConnectToHost(string ip, int port)
    {
        if (tcpSocket != null) return;
        serverIp = ip;
        serverPort = port;

        //为类型解析器注册表添加数据
        messageTypeRegistry[MessageType.worldSnapshot] = WorldSnapshot.Parser;
        messageTypeRegistry[MessageType.playerAssigned] = PlayerAssigned.Parser;
        messageTypeRegistry[MessageType.startGame] = StartGame.Parser;
        messageTypeRegistry[MessageType.beginMatch] = BeginMatch.Parser;
        //为消息处理器注册表添加数据
        handlersRegistry[typeof(WorldSnapshot)] = (message) => { Handle_WorldSnapshot((WorldSnapshot)message); }; 
        handlersRegistry[typeof(PlayerAssigned)] = (message) => { Handle_PlayerAssigned((PlayerAssigned)message); };
        handlersRegistry[typeof(StartGame)] = _ => Handle_StartGame();
        handlersRegistry[typeof(BeginMatch)] = _ => Handle_BeginMatch();
        
        try
        {
            udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

            tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcpSocket.BeginConnect(IPAddress.Parse(serverIp), serverPort, OnConnected, null);
        }
        catch (Exception error)
        {
            Debug.LogError("发起连接失败：" + error.Message);
            StopSending();
        }
    }

    private void OnConnected(IAsyncResult result)
    {
        try
        {
            tcpSocket.EndConnect(result);
            if (stopping) return;
            tcpSendThread = new Thread(TcpSendLoop) { IsBackground = true };
            tcpSendThread.Start();

            tcpReceiveThread = new Thread(TcpReceiveLoop) { IsBackground = true };
            tcpReceiveThread.Start();

            udpReceiveThread = new Thread(UdpReceiveLoop) { IsBackground= true };
            udpReceiveThread.Start();

            connected = true;
            Debug.Log("已连接房主");
            // 下一步：开始接收房主消息。
        }
        catch (ObjectDisposedException) { } // 退出游戏时可能已经关闭。
        catch (SocketException error)
        {
            Debug.LogError("连接失败：" + error.SocketErrorCode);
            StopSending();
        }
    }
    //-----------------------------------------------------------------------发送循环和接收循环
    private void TcpSendLoop()
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
                    int sent = tcpSocket.Send(packet, offset, packet.Length - offset, SocketFlags.None);
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
    private void TcpReceiveLoop()
    {
        try
        {
            while (!stopping)
            {
                // 长度包含两字节类型编号和正文，不包含长度字段自身
                byte[] header = new byte[4];
                MessageCodec.ReceiveExactly(tcpSocket, header);
                int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
                if (length < 2 || length > MessageCodec.MaxMessageLength) throw new System.IO.IOException("消息长度不合法");

                byte[] body = new byte[length];
                MessageCodec.ReceiveExactly(tcpSocket, body);
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

    //只接收世界快照
    private void UdpReceiveLoop()
    {
        byte[] buffer = new byte[65535];
        try
        {
            while (!stopping)
            {
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                int count = udpSocket.ReceiveFrom(buffer, ref sender);
                if (count < 2 || !tcpSocket.RemoteEndPoint.Equals(sender)) continue;

                MessageType type = (MessageType)((buffer[0] << 8) | buffer[1]);
                if (type != MessageType.worldSnapshot) continue;

                try { receiveQueue.Enqueue(WorldSnapshot.Parser.ParseFrom(buffer, 2, count - 2)); }
                catch (InvalidProtocolBufferException) { } // 丢掉坏包，继续接收。
            }
        }
        catch (SocketException ex) { if (!stopping) Debug.LogWarning($"UDP 接收失败：{ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) when (stopping) { }
    }

    //-----------------------------------------------------------------------其他生命周期函数

    protected override void Update()
    {
        base.Update();

        

        while(receiveQueue.TryDequeue(out IMessage message))
        {
            HandleMessage(message);
        }

        clock.Advance(Time.unscaledDeltaTime);

        //对其他玩家，敌人，场景进行插值
        InterpolateSceneState();

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
    /// <summary>
    /// 每次收到世界快照要做的事
    /// </summary>
    private void Handle_WorldSnapshot(WorldSnapshot snapshot)
    {
        if (snapshot == null || SceneManager.GetActiveScene().name != "GameScene") 
            return;

        SyncPlayerRoster(snapshot);

        clock.OnSnapshotArrive(snapshot.Tick);
        snapshotBuffer.Add(snapshot.Tick, snapshot);

        //对本地玩家开始回滚重演
        if (snapshot.PlayerSnapshots.TryGetValue(localPlayerId, out var localState))
            ReconcileLocalPlayer(localState);

        
    }

    private void Handle_PlayerAssigned(PlayerAssigned playerAssigned)
    {
        localPlayerId = playerAssigned.PlayerId;
        Debug.Log($"房主分配的玩家 ID：{localPlayerId}");

        if (localPlayerId <= 0) return;

        byte[] packet = MessageCodec.EncodeUdpMessage(MessageType.udpHello, new UdpHello { PlayerId = localPlayerId });
        var host = new IPEndPoint(IPAddress.Parse(serverIp), serverPort);
        try { udpSocket.SendTo(packet, SocketFlags.None, host); }
        catch (SocketException ex) { if (!stopping) Debug.LogWarning($"UdpHello 发送失败：{ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) when (stopping) { }
    }
    
    private void Handle_StartGame()
    {
        if (startSceneRequested) return;
        if (sceneSwitcher == null)
        {
            Debug.LogError("GuestClient 未绑定 SceneSwitcher。", this);
            return;
        }
    
        if (!sceneSwitcher.StartGame(OnLocalSceneReady))
        {
            Debug.LogError("游客无法加载游戏场景，请检查过场 UI 和场景配置。", this);
            return;
        }
        startSceneRequested = true;
        DontDestroyOnLoad(gameObject);
    }
    //-----------------------------------------------------------------------杂项函数
    

    private void OnLocalSceneReady(Scene scene)
    {
        if (!HasJoinedRoom || localSceneReady) return;
        localSceneReady = true;
        sendQueue.Enqueue(MessageCodec.EncodeTcpMessage(MessageType.sceneReady, new SceneReady()));
        sendSignal.Set();
    }

    private void Handle_BeginMatch()
    {
        if (localSceneReady && sceneSwitcher != null)
            sceneSwitcher.TryEnterGame();
    }

    private void SyncPlayerRoster(WorldSnapshot snapshot)
    {
        //前半部分：删除过时的玩家
        //由于udp的性质，老的快照可能后到，跳过这种情况
        if (latestRosterTick.HasValue && snapshot.Tick <= latestRosterTick.Value) 
            return;

        latestRosterTick = snapshot.Tick;

        departedPlayers.Clear();
        foreach (int id in players.Keys)
        {
            if (!snapshot.PlayerSnapshots.ContainsKey(id)) 
                departedPlayers.Add(id);
        }

        foreach (int id in departedPlayers) 
        {
            Destroy(players[id]); players.Remove(id); 
        }

        //后半部分：添加新加入的玩家
        foreach (var pair in snapshot.PlayerSnapshots)
        {
            var position = pair.Value.Position;

            //如果已经快照里的玩家加入了players，就跳过
            if (position == null || players.ContainsKey(pair.Key))
                continue;

            GameObject prefab = Resources.Load<GameObject>($"Prefabs/Player_{pair.Key}");

            if (prefab == null)
            {
                Debug.LogError($"找不到Player_{pair.Key}对应的预制体");
                continue;
            }

            var player = Instantiate(prefab, new Vector3(position.X, position.Y, position.Z), Quaternion.identity);
            player.name = $"Player_{pair.Key}";
            players.Add(pair.Key, player);
        }
    }

    protected override void SendMoveInput_And_AimAngle()
    {
        if (!connected || stopping || localPlayerId <= 0 || sendingUdpInput) return;

        byte[] packet = MessageCodec.EncodeUdpMessage(MessageType.move_Input, playerMove);
        var host = new IPEndPoint(IPAddress.Parse(serverIp), serverPort);
        _ = SendUdpInputAsync(packet, host);
    }

    private void InterpolateSceneState()
    {
        if(snapshotBuffer.TrySample(clock.RenderTick,out WorldSnapshot from,out WorldSnapshot to,out double alpha))
        {
            foreach(var player in players)
            {
                int id = player.Key;
                //跳过本地玩家
                if (id == localPlayerId || player.Value == null) continue;
                if (!from.PlayerSnapshots.TryGetValue(id, out var playerSnapshot_from) || !to.PlayerSnapshots.TryGetValue(id, out var playerSnapshot_to)) continue;

                var a = playerSnapshot_from.Position;
                var b = playerSnapshot_to.Position;
                if (a == null || b == null) continue;

                Vector3 fromPosition = new Vector3(a.X, a.Y, a.Z);
                Vector3 toPosition = new Vector3(b.X, b.Y, b.Z);
                player.Value.transform.position = Vector3.Lerp(fromPosition, toPosition, (float)alpha);
            }
        }
    }

    /// <summary>
    /// 重演并删除权威确认过的快照历史
    /// </summary>
    private void ReconcileLocalPlayer(PlayerSnapshot snapshot)
    {
        double ack = snapshot.TimeStamp;
        if (ack<=lastReconciledTimeStamp || snapshot.Position == null) return;
        if (!players.TryGetValue(localPlayerId, out var player)) return;

        //position是重算后的理想位置
        Vector3 position = new Vector3(snapshot.Position.X, snapshot.Position.Y, snapshot.Position.Z);
        List<double> confirmedKeys = new List<double>();

        foreach(var history in prediction_history)
        {
            if(ack >= history.Key)
            {
                confirmedKeys.Add(history.Key);
                continue;
            }

            var(moveInput,deltaTime) = history.Value;
            position += new Vector3(moveInput.InputX *3* deltaTime, 0f, moveInput.InputZ *3* deltaTime);
        }

        Vector3 correctingValue = position - player.transform.position;
        player.transform.position += 0.1f * correctingValue;
        foreach (double key in confirmedKeys) 
            prediction_history.Remove(key);

        lastReconciledTimeStamp = ack;
    }

    private async Task SendUdpInputAsync(byte[] packet, IPEndPoint host)
    {
        sendingUdpInput = true;
        try 
        {
            await udpSocket.SendToAsync(new ArraySegment<byte>(packet), SocketFlags.None, host); 
        }
        catch (SocketException ex) 
        {
            if (!stopping) Debug.LogWarning($"UDP 输入发送失败：{ex.SocketErrorCode}"); 
        }
        catch (ObjectDisposedException) when (stopping) { }
        finally { sendingUdpInput = false; }
    }

    protected override void SendSomeRequests()
    {

    }

    private void StopSending()
    {
        stopping = true;
        connected = false;
        sendSignal.Set(); // 唤醒可能正在等待的线程，让它检查退出标志。
        tcpSocket?.Close();  // 解除可能正在阻塞的 Send。
        udpSocket?.Close();
    }

    /// <summary>
    /// 为当前帧的playerMove的tickStamp赋值，并将其加入moveInput_history
    /// </summary>
    protected override void MoveInput_And_Prediction()
    {
        //如果时钟还没有更新，那移动输入也没有意义，直接返回
        if (!clock.IsInitialized || !players.TryGetValue(localPlayerId, out var localPlayer) || localPlayer == null) 
          return;

        base.MoveInput_And_Prediction();

        //unscaledTime的双精度版本
        double tickStamp = Time.unscaledTimeAsDouble;
        playerMove.TimeStamp = tickStamp;

        SendMoveInput_And_AimAngle();

        //开始预测
        //之后会换成controller.move()
        Vector3 displacement = new Vector3 { x = moveInput.x *3* Time.deltaTime, y = 0, z = moveInput.y *3* Time.deltaTime };
        localPlayer.transform.position += displacement;

        prediction_history.Add(tickStamp, (new PlayerMove(playerMove),Time.deltaTime));
    }
}



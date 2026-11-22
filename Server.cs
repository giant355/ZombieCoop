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

public class Server : MonoBehaviour
{
    public static int RoomPort;


    //用来进行计算和模拟的类，免得一些繁杂的无关变量（游戏场景逻辑的巨量变量）都放在服务器
    private SimulationWorld simulationWorld;
    private WorldSnapshot snapShotToSend = null;


    private Socket tcpSocket;
    private Socket udpSocket;
    private volatile bool stopping = false;
    private readonly object acceptGate = new object();
    public RoomPhase Phase { get; private set; } = RoomPhase.Offline;


    //记录玩家最新的输入
    private readonly Dictionary<int, PlayerMove> clientsLastestInputs = new Dictionary<int, PlayerMove>();
    public Dictionary<int, PlayerMove> ClientLastestInputs => clientsLastestInputs;
    //连接回调只把 Socket 入队，主线程再登记
    private readonly ConcurrentQueue<Socket> clientSocketsQueue= new ConcurrentQueue<Socket>();
    //断线队列
    private readonly ConcurrentQueue<Socket> disconnectedClients = new ConcurrentQueue<Socket>();
    private readonly ConcurrentQueue<(Socket Sender, IMessage Message)> receiveQueue = new ConcurrentQueue<(Socket, IMessage)>();
    private readonly Dictionary<Socket, ConcurrentQueue<byte[]>> sendQueues = new Dictionary<Socket, ConcurrentQueue<byte[]>>();
    private readonly List<Socket> clientSocketsList = new List<Socket>();

    /// <summary>主线程已登记的游客数，不含房主。</summary>
    public int ConnectedGuestCount => clientSocketsList.Count;


    private readonly Dictionary<Socket, int> playerIds = new Dictionary<Socket, int>();
    private readonly ConcurrentDictionary<IPEndPoint, int> udpEndpoints = new ConcurrentDictionary<IPEndPoint, int>();
    private readonly ConcurrentQueue<(int PlayerId, IPEndPoint Endpoint)> udpHellos = new ConcurrentQueue<(int, IPEndPoint)>();
    private readonly ConcurrentQueue<(IPEndPoint Endpoint, PlayerMove Move)> udpMoves = new ConcurrentQueue<(IPEndPoint, PlayerMove)>();


    private Queue<int> GuestPlayerIds = new Queue<int>(new int[] { 1,2,3});


    //消息类型&解析器注册表
    private readonly Dictionary<MessageType, MessageParser> messageTypeRegistry = new Dictionary<MessageType, MessageParser>();
    //消息处理器注册表
    private readonly Dictionary<Type, Action<IMessage>> handlersRegistry = new Dictionary<Type, Action<IMessage>>();
    private readonly HashSet<Socket> loadingGuests = new HashSet<Socket>();
    private readonly HashSet<Socket> readyGuests = new HashSet<Socket>();

    public bool CanStartMatch => Phase == RoomPhase.Loading && simulationWorld != null &&
        readyGuests.Count == loadingGuests.Count;


    /// <summary>
    /// 发送中标记，防止同一个消息队列里面一条消息还没发完，另一条消息就开始发送，导致串行
    /// </summary>
    private readonly HashSet<Socket> sendingPlayers = new HashSet<Socket>();


    private WorldSnapshot pendingUdpSnapshot;
    private bool udpSnapshotSending;


    private void Awake()
    {
        DontDestroyOnLoad(this.gameObject);

    }

    public bool StartService()
    {
        if (Phase != RoomPhase.Offline) return false;
        try
        {
            stopping = false;

            //为类型解析器注册表添加数据
            messageTypeRegistry[MessageType.move_Input] = PlayerMove.Parser;
            messageTypeRegistry[MessageType.sceneReady] = SceneReady.Parser;

            //为消息处理器注册表添加数据


            tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcpSocket.Bind(new IPEndPoint(IPAddress.Any, 0));

            RoomPort = ((IPEndPoint)tcpSocket.LocalEndPoint).Port;

            udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udpSocket.Bind(new IPEndPoint(IPAddress.Any, RoomPort));

            tcpSocket.Listen(3);
            tcpSocket.BeginAccept(AcceptCallback, null);

            Phase = RoomPhase.Lobby;

            Thread udpReceiveThread = new Thread(() => UdpReceiveLoop());
            udpReceiveThread.IsBackground = true;
            udpReceiveThread.Start();

            //添加房主input
            clientsLastestInputs[0] = new PlayerMove();
            //simulationWorld.AddPlayer(0);

            return true;
        }
        catch (Exception error)
        {
            stopping = true;

            tcpSocket?.Close();
            tcpSocket = null;

            udpSocket?.Close();
            udpSocket = null;

            clientsLastestInputs.Remove(0);
            Phase = RoomPhase.Offline;

            Debug.LogError("创建房间失败：" + error.Message);
            return false;
        }
    }

    // Update is called once per frame
    void Update()
    {
        //断线处理
        while (disconnectedClients.TryDequeue(out Socket socket))
        {
            if (!playerIds.TryGetValue(socket, out int playerId)) continue;

            clientSocketsList.Remove(socket);
            playerIds.Remove(socket);
            loadingGuests.Remove(socket);
            readyGuests.Remove(socket);
            sendQueues.Remove(socket);
            clientsLastestInputs.Remove(playerId);

            GuestPlayerIds.Enqueue(playerId);

            foreach(var pair in udpEndpoints)
            {
                if(pair.Value == playerId)
                udpEndpoints.TryRemove(pair.Key,out _);
            }

            if (simulationWorld != null)
            {
                simulationWorld.RemovePlayer(playerId);
            }

            Debug.Log($"玩家 {playerId} 已断开，连接和输入记录已清理");
        }

        while (clientSocketsQueue.TryDequeue(out Socket socket))
        {
            //游戏开局便不再接受连接
            if (Phase != RoomPhase.Lobby || GuestPlayerIds.Count == 0)
            {
                socket.Close();
                Debug.Log(Phase == RoomPhase.Lobby ? "房间已满，拒绝新连接" : "不在大厅，拒绝新连接");
                continue;
            }

            clientSocketsList.Add(socket);
            playerIds.Add(socket, GuestPlayerIds.Dequeue());

            Thread clientReceiveThread = new Thread(()=>TcpReceiveLoop(socket));
            clientReceiveThread.IsBackground = true;
            clientReceiveThread.Start();

            //为输入列表注册元素
            clientsLastestInputs.Add(playerIds[socket], new PlayerMove());
            sendQueues.Add(socket,new ConcurrentQueue<byte[]>());

            //对新连接的客户端发送第一条tcp消息，后续期待对方发送一条udp消息
            var assigned = new PlayerAssigned { PlayerId = playerIds[socket] };
            sendQueues[socket].Enqueue(MessageCodec.EncodeTcpMessage(MessageType.playerAssigned, assigned));
            Debug.Log($"房主：玩家 {assigned.PlayerId} 的分配消息已入队");
            
            if (simulationWorld != null)
            {
                simulationWorld.AddPlayer(playerIds[socket]);
            }
        }

        while (udpHellos.TryDequeue(out var hello))
        {
            if (hello.PlayerId <= 0 || !playerIds.ContainsValue(hello.PlayerId)) continue;
            if (udpEndpoints.ContainsKey(hello.Endpoint)) continue;

            udpEndpoints.TryAdd(hello.Endpoint, hello.PlayerId);
            Debug.Log($"玩家 {hello.PlayerId} 的 UDP 地址已绑定：{hello.Endpoint}");
        }

        while (udpMoves.TryDequeue(out var input))
        {
            if (!udpEndpoints.TryGetValue(input.Endpoint, out int playerId)) continue;
            if (!clientsLastestInputs.ContainsKey(playerId)) continue;
            if (input.Move.TimeStamp <= clientsLastestInputs[playerId].TimeStamp) continue;

            clientsLastestInputs[playerId] = input.Move;
        }

        while (receiveQueue.TryDequeue(out var received))
        {
            if (!playerIds.ContainsKey(received.Sender)) continue;
            if (received.Message is SceneReady)
            {
                if (Phase == RoomPhase.Loading && loadingGuests.Contains(received.Sender) && readyGuests.Add(received.Sender))
                    Debug.Log($"玩家 {playerIds[received.Sender]} 已加载场景");
                continue;
            }
            HandleMessage(received.Message);
        }

        foreach (var clientSocket in clientSocketsList)
        {
            _ = SendLoopAsync(clientSocket);
        }

        //simulate
        if (Phase != RoomPhase.Playing || simulationWorld == null) return;

        snapShotToSend = simulationWorld.Simulate();
    }

    private void AcceptCallback(IAsyncResult result)
    {
        try
        {
            Socket client = tcpSocket.EndAccept(result);

            lock (acceptGate)
            {
                if (stopping)
                {
                    client.Close();
                    return;
                }

                clientSocketsQueue.Enqueue(client);
            }
        }
        catch (Exception ex)
        {
            if (!stopping) Debug.LogWarning("接受连接失败：" + ex.Message);
        }
        finally
        {
            lock (acceptGate)
            {
                if (!stopping)
                {
                    try
                    {
                        tcpSocket.BeginAccept(AcceptCallback, null);
                    }
                    catch (Exception ex)
                    {
                        if (!stopping) Debug.LogWarning("继续等待连接失败：" + ex.Message);
                    }
                }
            }
        }
    }
    //五十tick每秒
    private void FixedUpdate()
    {
        if (snapShotToSend == null) return;

        BroadcastUdpSnapshot(snapShotToSend);
        simulationWorld.TickAdvance();

        //清空
        //foreach (PlayerMove playerMove in clientsLastestInputs.Values)
        //{
        //    playerMove.InputX = 0;
        //    playerMove.InputZ = 0;
        //    playerMove.TimeStamp = 0;
        //}
    }

    private void OnDestroy()
    {
        lock (acceptGate)
        {
            stopping = true;
        }

        tcpSocket?.Close();
        udpSocket?.Close();

        foreach (Socket client in clientSocketsList) client.Close();
            clientSocketsList.Clear();

        while (clientSocketsQueue.TryDequeue(out Socket pending)) 
            pending.Close();
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
    //-----------------------------------------------------------------------
    /// <summary>锁住大厅名单，准备加载游戏场景</summary>
    public bool TryBeginLoading()
    {
        if (Phase != RoomPhase.Lobby) return false;
        Phase = RoomPhase.Loading;
        loadingGuests.Clear();
        readyGuests.Clear();
        foreach (Socket guest in clientSocketsList) loadingGuests.Add(guest);

        Boardcast(MessageCodec.EncodeTcpMessage(MessageType.startGame, new StartGame()));

        return true;
    }
    //-----------------------------------------------------------------------杂项函数
    private void Boardcast(byte[] bytes)
    {
        foreach(var queue in sendQueues.Values)
        {
            queue.Enqueue(bytes);
        }
    }

    private void BroadcastUdpSnapshot(WorldSnapshot snapshot)
    {
        if (udpEndpoints.Count == 0) return;

        pendingUdpSnapshot = snapshot;

        if (!udpSnapshotSending) 
            _ = SendUdpSnapshotsAsync();
    }

    private async Task SendUdpSnapshotsAsync()
    {
        udpSnapshotSending = true;
        try
        {
            while (!stopping && pendingUdpSnapshot != null)
            {
                WorldSnapshot snapshot = pendingUdpSnapshot;
                pendingUdpSnapshot = null;
                byte[] packet = MessageCodec.EncodeUdpMessage(MessageType.worldSnapshot, snapshot);

                // 等待发送期间可能有玩家断线，所以不跨 await 遍历原字典
                //迭代器遍历期间，容器不能变，所以提前拷贝
                var playerEndPointsToSend = new List<IPEndPoint>(udpEndpoints.Keys);
                foreach (IPEndPoint iPEndPoint in playerEndPointsToSend)
                {
                    if (stopping) return;
                    //可能在这个线程刚准备发送，客户端就已经断线从主线程移除，所以再检查一次
                    if (!udpEndpoints.TryGetValue(iPEndPoint, out int playerId)) continue;

                    try 
                    {
                        await udpSocket.SendToAsync(new ArraySegment<byte>(packet), SocketFlags.None, iPEndPoint); 
                    }
                    catch (SocketException ex) 
                    {
                        if (!stopping) Debug.LogWarning($"UDP 快照发送失败：{ex.SocketErrorCode}"); 
                    }
                }
            }
        }
        catch (ObjectDisposedException) when (stopping) { }
        finally { udpSnapshotSending = false; }
    }

    private void TcpReceiveLoop(Socket clientSocket)
    {
        try
        {
            while (!stopping)
            {
                // 长度包含两字节类型编号和正文，不包含长度字段自身
                byte[] header = new byte[4];
                MessageCodec.ReceiveExactly(clientSocket, header);
                int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
                if (length < 2 || length > MessageCodec.MaxMessageLength) 
                    throw new System.IO.IOException("消息长度不合法");

                byte[] body = new byte[length];
                MessageCodec.ReceiveExactly(clientSocket, body);
                if (stopping) 
                    break;

                MessageType messageType = (MessageType)((body[0] << 8) | body[1]);
                // 按类型编号寻找解析器；未注册的类型跳过
                if (messageTypeRegistry.TryGetValue(messageType, out MessageParser parser))
                {
                    // 使用找到的解析器，跳过两字节类型编号，解析剩余正文
                    IMessage message = parser.ParseFrom(body, 2, body.Length - 2);
                    // 交给主线程处理
                    receiveQueue.Enqueue((clientSocket, message));
                }
            }
        }
        catch (Exception ex)
        {
            if (!stopping) Debug.LogWarning("接收失败：" + ex.Message);
        }
        finally
        {
            clientSocket.Close();
            disconnectedClients.Enqueue(clientSocket);
        }
    }

    /// <summary>
    /// 只接收udpHello  或MoveInput  或AimAngle
    /// </summary>
    private void UdpReceiveLoop()
    {
        byte[] buffer = new byte[65535];
        try
        {
            while (!stopping)
            {
                EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                int count = udpSocket.ReceiveFrom(buffer, ref sender);
                if (count < 2 || !(sender is IPEndPoint endpoint)) continue;
                MessageType type = (MessageType)((buffer[0] << 8) | buffer[1]);

                try
                {
                    switch (type)
                    {
                        case MessageType.udpHello:
                            UdpHello hello = UdpHello.Parser.ParseFrom(buffer, 2, count - 2);
                            udpHellos.Enqueue((hello.PlayerId, endpoint));
                            break;
                        case MessageType.move_Input:
                            PlayerMove playerMove = PlayerMove.Parser.ParseFrom(buffer, 2, count - 2);
                            udpMoves.Enqueue((endpoint, playerMove));
                            break;
                        case MessageType.aimAngle:
                            //TODO
                            break;

                        default:
                            break;
                    }
                }
                catch (InvalidProtocolBufferException) { } // 忽略坏包，继续收下一包。
            }
        }
        catch (SocketException ex) { if (!stopping) Debug.Log($"UDP 接收失败：{ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { if (!stopping) Debug.Log("UDP Socket 意外关闭。"); }
    }

    private async Task SendAllAsync(Socket socket, byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int sent = await socket.SendAsync(new ArraySegment<byte>(data, offset, data.Length - offset), SocketFlags.None);
            if (sent == 0) throw new System.IO.IOException("无法继续发送。");
            offset += sent;
        }
    }

    /// <summary>
    /// 创建循环，把指定队列发空为止
    /// </summary>
    private async Task SendLoopAsync(Socket clientSocket)
    {
        if (sendingPlayers.Contains(clientSocket))
            return;

        sendingPlayers.Add(clientSocket);

        try
        {
            if (!sendQueues.TryGetValue(clientSocket, out ConcurrentQueue<byte[]> queue))
                return;

            while(queue.TryDequeue(out byte[] bytes))
            {
                await SendAllAsync(clientSocket, bytes);
            }
        }
        catch(Exception ex)
        {
            if (!stopping) 
                Debug.LogWarning($"发送失败：{ex.Message}");

            sendQueues.Remove(clientSocket);
            clientSocket.Close();
            disconnectedClients.Enqueue(clientSocket);
        }
        finally
        {
            sendingPlayers.Remove(clientSocket);
        }
    }

    /// <summary>房主本地场景加载完成，但尚未进入比赛。</summary>
    public void MarkLocalSceneReady(Scene scene)
    {
        if (Phase != RoomPhase.Loading || !scene.isLoaded || simulationWorld != null) return;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var world = root.GetComponentInChildren<SimulationWorld>(true);
            if (world == null) continue;
            world.Initialize(this);
            simulationWorld = world;
            return;
        }
        Debug.LogError("GameScene 中没有找到 SimulationWorld。");
    }

    /// <summary>仅在所有当前房间成员就绪后接受房主确认。</summary>
    public bool TryBeginMatch(SceneSwitcher switcher)
    {
        if (!CanStartMatch || switcher == null || !switcher.TryEnterGame()) return false;
        simulationWorld.AddPlayer(0);
        foreach (int playerId in playerIds.Values) simulationWorld.AddPlayer(playerId);
        Phase = RoomPhase.Playing;
        Boardcast(MessageCodec.EncodeTcpMessage(MessageType.beginMatch, new BeginMatch()));
        Debug.Log("游戏已开始：Playing");
        return true;
    }
}

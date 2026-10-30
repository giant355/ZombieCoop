using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

public class clientInput
{

}
public class Server : MonoBehaviour
{
    //用来进行计算和模拟的类，免得一些繁杂的无关变量（游戏场景逻辑的巨量变量）都放在服务器
    [SerializeField]private SimulationWorld simulationWorld;
    Socket listener;
    private ulong tick = 0;
    private List<clientInput> clientsLastestInput = new List<clientInput>();
    private List<Socket> clientSocketsList= new List<Socket>();
    private void Awake()
    {
        DontDestroyOnLoad(this.gameObject);
    }
    void Start()
    {
        listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 5050));
        listener.Listen(3);
        listener.BeginAccept(AcceptCallback, null);

        //TODO：点击开始游戏，SceneSwitcher切换场景
    }

    // Update is called once per frame
    void Update()
    {

    }

    private void AcceptCallback(IAsyncResult result)
    {
        if(clientSocketsList.Count >= 3)
        {
            //ui prints "the room is full"
            print("the room is full");
            return;
        }

        Socket client = listener.EndAccept(result);

        clientSocketsList.Add(client);

        listener.BeginAccept(AcceptCallback, null);
    }
    //五十tick每秒
    private void FixedUpdate()
    {
        tick++;

        simulationWorld.Simulate();
        Boardcast();
    }

  
    private void Boardcast()
    {

    }
}

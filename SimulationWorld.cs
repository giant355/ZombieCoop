using HostAuthorityDemo.Protocol;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class SimulationWorld : MonoBehaviour
{
    //需要被模拟的变量在这里



    private Dictionary<int,PlayerMove> clientsLastestInput;


    //玩家列表
    //以后会改成Fps Controller Rig
    //TODO
    private readonly Dictionary<int, GameObject> players = new Dictionary<int, GameObject>();


    private ulong tick = 0;

    /// <summary>由服务器在游戏场景加载后提供玩家输入表</summary>
    public void Initialize(Server server)
    {
        clientsLastestInput = server.ClientLastestInputs;
    }

    void Update()
    {
        
    }

    public WorldSnapshot Simulate()
    {
        WorldSnapshot worldSnapshot = new WorldSnapshot();
        worldSnapshot.Tick = tick;

        SimulateEnemies(worldSnapshot);
        SimulatePlayers(worldSnapshot);

        return worldSnapshot;
    }

    private void SimulateEnemies(WorldSnapshot worldSnapshot)
    {

    }

    private void SimulatePlayers(WorldSnapshot worldSnapshot)
    {
        //TODO
        foreach (var keyValuePair in players)
        {
            int id = keyValuePair.Key;
            GameObject player = keyValuePair.Value;

            Vector3 displacement = new Vector3(clientsLastestInput[id].InputX *3* Time.deltaTime, 0, clientsLastestInput[id].InputZ *3* Time.deltaTime);
            player.transform.position += displacement;

            Vector3 position = player.transform.position;
            worldSnapshot.PlayerSnapshots.Add(id, new PlayerSnapshot { Position = new Postion { X = position.x, Y = position.y, Z = position.z },
                                                                       TimeStamp = clientsLastestInput[id].TimeStamp });
        }
    }

    //---------------------------------------------------------供服务器调用的方法
    /// <summary>创建并登记指定玩家的权威角色；重复登记时不再创建。</summary>
    public void AddPlayer(int playerId)
    {
        if (players.ContainsKey(playerId)) return;

        GameObject prefab = Resources.Load<GameObject>($"Prefabs/Player_{playerId}");

        if (prefab == null) { Debug.LogError("找不到预制体"); return; }

        GameObject player = Instantiate(prefab,prefab.transform.position, Quaternion.identity);
        player.name = $"Player_{playerId}";
        players.Add(playerId, player);
    }

    /// <summary>移除指定玩家的登记，并销毁其权威角色；玩家不存在时不处理。</summary>
    public void RemovePlayer(int playerId)
    {
        if (!players.TryGetValue(playerId, out GameObject player)) return;

        players.Remove(playerId);
        Destroy(player);
    }

    public void TickAdvance()
    {
        tick++;
    }
}

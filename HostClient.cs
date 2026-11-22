using HostAuthorityDemo.Protocol;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HostClient : Client
{
    [SerializeField] private Server server;
    private Dictionary<int, PlayerMove> clientsLastestInputs;


    // Start is called before the first frame update
    void Start()
    {
        if (server == null)
            Debug.LogError("房主没有分配服务器");

        clientsLastestInputs = server.ClientLastestInputs;
    }

    // Update is called once per frame
    protected override void Update()
    {
        base.Update();
    }
    protected override void SendMoveInput_And_AimAngle()
    {
        clientsLastestInputs[0] = playerMove;
    }

    protected override void SendSomeRequests()
    {
        // TODO: implement host-local input/request submission.
    }

    protected override void MoveInput_And_Prediction()
    {
        base.MoveInput_And_Prediction();

        SendMoveInput_And_AimAngle();
    }
}

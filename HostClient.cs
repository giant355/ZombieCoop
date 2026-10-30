using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class HostClient : Client
{

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    protected override void Update()
    {
        base.Update();
    }
    protected override void SendMoveInput_And_AimAngle()
    {
        // TODO: implement host-local input/request submission.
    }

    protected override void SendSomeRequests()
    {
        // TODO: implement host-local input/request submission.
    }
}

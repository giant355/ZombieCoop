using HostAuthorityDemo.Protocol;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>本机玩家的客户端公共流程；不负责权威模拟。</summary>
public abstract class Client : MonoBehaviour
{
    protected Vector2 MoveInput;
    protected Quaternion AimAngle;
    protected virtual void Update()
    {
        float x = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float z = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        MoveInput = Vector2.ClampMagnitude(new Vector2(x, z), 1f);
        //TODO
        AimAngle = Quaternion.identity;
        SendMoveInput_And_AimAngle();
    }

    protected abstract void SendMoveInput_And_AimAngle();
    protected abstract void SendSomeRequests();
}
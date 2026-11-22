using HostAuthorityDemo.Protocol;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>本机玩家的客户端公共流程；不负责权威模拟。</summary>
public abstract class Client : MonoBehaviour
{
    protected Vector2 moveInput;
    protected PlayerMove playerMove;
    protected Quaternion AimAngle;
    protected virtual void Update()
    {
        MoveInput_And_Prediction();
    }

    protected abstract void SendMoveInput_And_AimAngle();
    protected abstract void SendSomeRequests();
    /// <summary>
    /// 为当前帧的playerMove的x和z赋值
    /// 为当前帧的moveInput的x和z赋值
    /// </summary>
    protected virtual void MoveInput_And_Prediction()
    {
        float x = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float z = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        moveInput = Vector2.ClampMagnitude(new Vector2(x, z), 1f);

        //字典到时候会存储引用，所以要new
        playerMove = new PlayerMove { InputX = moveInput.x,InputZ = moveInput.y};
        //TODO
        AimAngle = Quaternion.identity;
    }
}
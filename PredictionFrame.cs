using UnityEngine;

/// <summary>记录一个模拟 Tick 使用的输入，以及该步结束后的预测位置。</summary>
public readonly struct PredictionFrame
{
    /// <summary>该步模拟结束时的 Tick 编号。</summary>
    public readonly ulong Tick;
    public readonly Vector2 MoveInput;
    public readonly Vector3 Position;


    public PredictionFrame(ulong tick, Vector2 moveInput, Vector3 position)
    {
        Tick = tick;
        MoveInput = moveInput;
        Position = position;
    }
}
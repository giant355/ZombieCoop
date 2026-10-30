using System.Collections.Generic;
using UnityEngine;

/// <summary>管理本机玩家的预测状态与历史；不负责网络收发和模型显示。</summary>
public sealed class ClientPrediction
{
    private readonly SortedList<ulong, PredictionFrame> history = new SortedList<ulong, PredictionFrame>();

    public bool IsInitialized { get; private set; }
    public ulong PredictedTick { get; private set; }
    public Vector3 PredictedPosition { get; private set; }

    /// <summary>以已知状态建立预测基线；清空旧历史，但不执行移动。</summary>
    public void Initialize(ulong tick, Vector3 position)
    {
        history.Clear();
        PredictedTick = tick;
        PredictedPosition = position;
        history.Add(tick, new PredictionFrame(tick, Vector2.zero, position));
        IsInitialized = true;
    }
}
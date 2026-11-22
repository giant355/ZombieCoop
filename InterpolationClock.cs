using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InterpolationClock
{
    private readonly int tickRate;
    private readonly int bufferDistance;


    private double renderTick;
    private double playbackSpeed = 1;
    private double smoothedDistance;
    private ulong latestSnapshotTick;


    private bool isInitialized;


    public bool IsInitialized => isInitialized;
    public double RenderTick => renderTick;
    public double PlaybackSpeed => playbackSpeed;
    public ulong LatestSnapshotTick => latestSnapshotTick;

    public InterpolationClock(int tickRate = 50, int bufferDistance = 4)
    {
        if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (bufferDistance < 0) throw new ArgumentOutOfRangeException(nameof(bufferDistance));

        this.tickRate = tickRate;
        this.bufferDistance = bufferDistance;
    }

    /// <summary>
    /// 维护和推进clock当前最新收到的世界快照tick
    /// 根据当前的renderTick与targetTick的距离来改变播放速度
    /// 承担第一次调用初始化函数的职责
    /// </summary>
    public void OnSnapshotArrive(ulong tick)
    {
        //检查是否初始化
        if(!isInitialized)
        {
            latestSnapshotTick = tick;
            renderTick = (double)latestSnapshotTick - bufferDistance;
            playbackSpeed = 1;
            smoothedDistance = bufferDistance;

            isInitialized = true;
            return;
        }

        //只有出现更新的tick才继续
        if(tick >  latestSnapshotTick)
        {
            latestSnapshotTick = tick;

            double distance = latestSnapshotTick - renderTick;
            smoothedDistance += (distance - smoothedDistance) * 0.1;

            double drift = smoothedDistance - bufferDistance;

            double resetThreshold = Math.Max(tickRate * 2.0, bufferDistance * 4.0);
            double targetTick = (double)latestSnapshotTick - bufferDistance;

            //如果超出范围
            if (Math.Abs(renderTick - targetTick) > resetThreshold)
            {
                renderTick = targetTick;
                smoothedDistance = bufferDistance;
                playbackSpeed = 1.0;
                return;
            }

            if      (drift >  1.0) playbackSpeed = 1.02;
            else if (drift < -1.0) playbackSpeed = 0.96;//更快的积累缓冲
            else                   playbackSpeed = 1.0;
        }
    }

    public void Advance(double deltaTime)
    {
        if (!isInitialized || double.IsNaN(deltaTime) || double.IsInfinity(deltaTime) || deltaTime < 0) return;

        renderTick += deltaTime * tickRate * playbackSpeed;
    }

    public void Reset()
    {
        isInitialized = false;
        latestSnapshotTick = 0;
        renderTick = 0;
        playbackSpeed = 1.0;
        smoothedDistance = 0;
    }
}

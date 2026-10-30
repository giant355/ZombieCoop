using System;
using System.Collections.Generic;

public sealed class SnapshotBuffer<T>
{
    private readonly SortedList<ulong, T> snapshots = new SortedList<ulong, T>();
    private int capacity { get; }


    public SnapshotBuffer(int capacity = 128)
    {
        //因为要插值，所以至少容量为2
        if(capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
    }

    public bool Add(ulong tick,T snapshot)
    {
        if(snapshots.ContainsKey(tick)) return false;
        //不可在容量满的时候添加tick比所保存的tick都小的快照
        if(snapshots.Count >= capacity && tick < snapshots.Keys[0]) return false;

        snapshots.Add(tick, snapshot);

        if(snapshots.Count >  capacity) snapshots.RemoveAt(0);
        return true;
    }

    public bool TrySample(double renderTick,out T from,out T to,out double alpha)
    {
        if (double.IsNaN(renderTick) || double.IsInfinity(renderTick)) throw new ArgumentOutOfRangeException(nameof(renderTick));

        from = default(T);
        to = default(T);
        alpha = 0;

        if (snapshots.Count == 0) return false;

        //renderTick在左边界以左的情况
        if(renderTick <= (double)snapshots.Keys[0])
        {
            from = to = snapshots.Values[0];
            return true;
        }

        int last = snapshots.Count - 1;
        //renderTick在右边界以右的情况
        if (renderTick >= (double)snapshots.Keys[last])
        {
            from = to = snapshots.Values[last];
            // 只保留最新端点，供后续快照到达后继续插值。
            for (int i = 0; i < last; i++) snapshots.RemoveAt(0);
            return true;
        }

        //renderTick在边界内的正常情况
        for (int i = 0; i < last; i++)
        {
            double leftTick = snapshots.Keys[i];
            double rightTick = snapshots.Keys[i + 1];
            if (renderTick < leftTick || renderTick > rightTick) continue;

            from = snapshots.Values[i];
            to = snapshots.Values[i + 1];
            alpha = (float)((renderTick - leftTick) / (rightTick - leftTick));

            // 只删除左端点之前的样本，保留当前左右端点及未来快照。
            for (int remove = 0; remove < i; remove++) 
                snapshots.RemoveAt(0);

            return true;
        }
        return false;
    }

    public void Clear() 
    {
        snapshots.Clear(); 
    }
}
/// <summary>房间从创建到结束的运行阶段。</summary>
public enum RoomPhase
{
    Offline, // 没有运行中的房间
    Lobby,   // 等待开局，允许加入
    Loading, // 名单已锁定，正在加载地图
    Playing, // 游戏中，允许世界模拟
    Ended    // 本局结束，等待清理
}

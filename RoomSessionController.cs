using UnityEngine;

/// <summary>协调菜单与房间流程，不负责底层收发或世界模拟。</summary>
public sealed class RoomSessionController : MonoBehaviour
{
    [SerializeField] private Server server;
    [SerializeField] private MainMenuUI mainMenu;
    [SerializeField] private GuestClient guestClient;

    private enum JoinView { None, Connecting, Joined, Failed, Disconnected }
    private JoinView displayedJoinView;
    private string joinEndpoint;
    private int displayedGuestCount = -1;
    private SceneSwitcher matchSwitcher;

    // 在 Unity 主线程更新文字；不从 Socket 回调直接访问 UI。
    private void LateUpdate()
    {
        if (mainMenu == null) return;
        if (server != null && server.Phase == RoomPhase.Lobby) RefreshGuestCount();
        if (matchSwitcher != null && server != null && server.Phase == RoomPhase.Loading)
            matchSwitcher.ShowHostReady(server.CanStartMatch);
        if (guestClient != null && joinEndpoint != null) RefreshJoinStatus();
    }

    private void RefreshGuestCount(bool force = false)
    {
        int count = server.ConnectedGuestCount;
        if (!force && displayedGuestCount == count) return;
        displayedGuestCount = count;
        mainMenu.SetCreateStatus($"已连接{count}个玩家");
    }

    private void RefreshJoinStatus(bool force = false)
    {
        bool hadJoined = displayedJoinView == JoinView.Joined || displayedJoinView == JoinView.Disconnected;
        JoinView next = guestClient.HasJoinedRoom ? JoinView.Joined
            : guestClient.ConnectionStopped ? (hadJoined ? JoinView.Disconnected : JoinView.Failed)
            : JoinView.Connecting;
        if (!force && next == displayedJoinView) return;
        displayedJoinView = next;

        switch (next)
        {
            case JoinView.Joined: mainMenu.SetJoinStatus($"已连接房主：{joinEndpoint}　玩家 ID：{guestClient.AssignedPlayerId}"); break;
            case JoinView.Disconnected: mainMenu.SetJoinStatus($"已与房主断开：{joinEndpoint}"); break;
            case JoinView.Failed: mainMenu.SetJoinStatus($"连接房主失败：{joinEndpoint}"); break;
            default: mainMenu.SetJoinStatus($"正在连接房主：{joinEndpoint}"); break;
        }
    }


    /// <summary>接收菜单的开房请求，启动房主服务器。</summary>
    public void CreateRoom()
    {
        if (server == null || mainMenu == null)
        {
            Debug.LogError("请配置 Server 和 MainMenu 引用。", this);
            return;
        }

        if (server.Phase != RoomPhase.Offline)
        {
            if (server.Phase == RoomPhase.Lobby) RefreshGuestCount(true);
            else mainMenu.SetCreateStatus("房间已经创建。");
            return;
        }

        server.gameObject.SetActive(true);
        server.enabled = true;

        if (!server.StartService())
        {
            mainMenu.SetCreateStatus("创建失败，请查看控制台。");
            return;
        }

        mainMenu.ShowHostAddress(Server.RoomPort);
        RefreshGuestCount(true);
        Debug.Log($"房间创建成功，阶段={server.Phase}");
    }

    /// <summary>接收菜单校验过的 IPv4:端口，并请求连接房主（在inspector里被joinAdressRequest绑定）</summary>
    public void JoinRoom(string endpoint)
    {
        if (guestClient == null || mainMenu == null)
        {
            Debug.LogError("请配置 GuestClient 和 MainMenu 引用。", this);
            return;
        }

        if (joinEndpoint != null)
        {
            RefreshJoinStatus(true);
            return;
        }

        string[] parts = endpoint.Split(':');
        joinEndpoint = endpoint;
        RefreshJoinStatus(true);
        guestClient.gameObject.SetActive(true);
        guestClient.ConnectToHost(parts[0], int.Parse(parts[1]));
    }

    /// <summary>房间创建后才允许加载游戏场景。</summary>
    public void RequestStartGame()
    {
        if (server == null || server.Phase != RoomPhase.Lobby)
        {
            mainMenu.SetCreateStatus("请先创建房间。");
            return;
        }

        matchSwitcher = GetComponent<SceneSwitcher>();
        if (matchSwitcher == null || !matchSwitcher.CanLoad)
        {
            Debug.LogError("请检查 SceneSwitcher、过场 UI 和游戏场景配置。", this);
            return;
        }

        if (!server.TryBeginLoading()) return;
        matchSwitcher.StartGame(server.MarkLocalSceneReady, ConfirmStartMatch);
    }

    private void ConfirmStartMatch()
    {
        server.TryBeginMatch(matchSwitcher);
    }
}

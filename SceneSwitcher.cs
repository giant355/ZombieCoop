using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>在主菜单后面加载游戏场景，直到房主确认才切入。</summary>
public class SceneSwitcher : MonoBehaviour
{
    [SerializeField] private string gameSceneName = "GameScene";
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private Slider loadingProgress;
    [SerializeField] private Button pressAnyKeyButton;

    private Scene menuScene;
    private Scene gameScene;
    private bool loading;
    private bool loaded;
    private bool entered;
    private int promptOpenedFrame;
    private Action hostConfirm;

    public bool CanLoad => !loading && !entered &&
        loadingPanel != null && loadingProgress != null && pressAnyKeyButton != null &&
        Application.CanStreamedLevelBeLoaded(gameSceneName);

    public bool StartGame(Action<Scene> onLoaded, Action confirm = null)
    {
        if (!CanLoad) return false;
        loading = true;
        menuScene = gameObject.scene;
        hostConfirm = confirm;
        pressAnyKeyButton.onClick.AddListener(ConfirmByHost);
        pressAnyKeyButton.gameObject.SetActive(false);
        loadingProgress.gameObject.SetActive(true);
        loadingPanel.SetActive(true);
        loadingProgress.value = 0f;
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);

        // 两个场景短时间共存，避免同时启用两个 AudioListener。
        foreach (GameObject root in menuScene.GetRootGameObjects())
            foreach (var listener in root.GetComponentsInChildren<AudioListener>())
                listener.enabled = false;

        StartCoroutine(LoadScene(onLoaded));
        return true;
    }

    private IEnumerator LoadScene(Action<Scene> onLoaded)
    {
        AsyncOperation operation = SceneManager.LoadSceneAsync(gameSceneName, LoadSceneMode.Additive);
        while (!operation.isDone)
        {
            loadingProgress.value = Mathf.Min(0.99f, operation.progress / 0.9f);
            yield return null;
        }

        gameScene = SceneManager.GetSceneByName(gameSceneName);
        loaded = gameScene.IsValid() && gameScene.isLoaded;
        loadingProgress.value = loaded ? 1f : 0f;
        if (loaded) onLoaded?.Invoke(gameScene);
    }

    /// <summary>此方法只由房主侧控制器调用，游客不会看到开局按钮。</summary>
    public void ShowHostReady(bool ready)
    {
        ready = ready && loaded && !entered && hostConfirm != null;
        if (pressAnyKeyButton.gameObject.activeSelf == ready) return;
        if (ready) promptOpenedFrame = Time.frameCount;
        loadingProgress.gameObject.SetActive(!ready);
        pressAnyKeyButton.gameObject.SetActive(ready);
    }

    private void Update()
    {
        if (hostConfirm != null && pressAnyKeyButton.gameObject.activeInHierarchy &&
            Time.frameCount > promptOpenedFrame && Input.anyKeyDown)
            ConfirmByHost();
    }

    private void ConfirmByHost()
    {
        if (!entered && pressAnyKeyButton.gameObject.activeInHierarchy)
            hostConfirm?.Invoke();
    }

    /// <summary>地图已经加载过；这里只切活动场景并移除主菜单。</summary>
    public bool TryEnterGame()
    {
        if (!loaded || entered || !SceneManager.SetActiveScene(gameScene)) return false;
        entered = true;
        loadingPanel.SetActive(false);
        SceneManager.UnloadSceneAsync(menuScene);
        return true;
    }
}

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>执行本机场景加载；是否允许开始由房主会话决定。</summary>
public class SceneSwitcher : MonoBehaviour
{
    [SerializeField] private string gameSceneName = "GameScene";

    /// <summary>供开始按钮调用；加载成功后由 Server 的 sceneLoaded 回调绑定世界。</summary>
    public void StartGame()
    {
        StartCoroutine(LoadScene());
    }
    private IEnumerator LoadScene()
    {
        AsyncOperation operation = SceneManager.LoadSceneAsync(gameSceneName);

        while(operation.isDone == false)
        {
            print("加载进度：" + operation.progress);
            yield return null;
        }
    }
}

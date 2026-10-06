using RaftModLoader;
using HMLLibrary;
using UnityEngine;
using MultiplayerTestHarness;

public class MultiplayerTest : Mod
{
    public void Start()
    {
        NetworkTestMenu.Install();

        if (NetworkTest.HostSessionExists())
        {
            NetworkTest.StartClient();
            Debug.Log("[MPTest] активный HOST найден - процесс запущен как CLIENT");
        }
        else
        {
            Debug.Log("[MPTest] мод загружен - Network Test доступен в меню мира");
        }
    }

    public void Update()
    {
        if (NetworkTest.IsActive)
            NetworkTest.Tick();
    }

    public void OnModUnload()
    {
        NetworkTest.Shutdown();
        NetworkTestMenu.Uninstall();
    }
}

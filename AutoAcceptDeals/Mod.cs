using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(AutoAcceptDeals.Mod), "AutoAcceptDeals", "0.1.0", "Steven Dilks")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace AutoAcceptDeals;

public class Mod : MelonMod
{
    private const string MainSceneName = "Main";
    private const string MenuSceneName = "Menu";
    private const string TutorialSceneName = "Tutorial";

    private const KeyCode ToggleKey = KeyCode.O;

    public override void OnInitializeMelon()
    {
        Settings.Load();
        LoggerInstance.Msg("AutoAcceptDeals loaded — enabled. Press O in-game to toggle.");
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        if (sceneName == MainSceneName)
        {
            if (!ModState.EnterScene()) return;
            LoggerInstance.Msg($"Entered game scene; mod {(ModState.Enabled ? "active" : "disabled")}.");
        }
        else if (sceneName == MenuSceneName || sceneName == TutorialSceneName)
        {
            if (!ModState.LeaveScene()) return;
            LoggerInstance.Msg("Left game scene; mod paused.");
        }
    }

    public override void OnUpdate()
    {
        if (!ModState.InGameScene) return;
        if (IsTextInputFocused()) return;

        if (Input.GetKeyDown(ToggleKey))
        {
            ModState.Toggle();
            LoggerInstance.Msg($"AutoAcceptDeals toggled: {(ModState.Enabled ? "ON" : "OFF")}");
        }
    }

    private static bool IsTextInputFocused()
    {
        var sel = EventSystem.current?.currentSelectedGameObject;
        if (sel == null) return false;
        if (sel.GetComponent<TMP_InputField>() != null) return true;
        if (sel.GetComponent<InputField>() != null) return true;
        return false;
    }
}

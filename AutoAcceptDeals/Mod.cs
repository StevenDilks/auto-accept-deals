using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Quests;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(AutoAcceptDeals.Mod), "AutoAcceptDeals", "0.1.1", "Steven Dilks")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace AutoAcceptDeals;

public class Mod : MelonMod
{
    private const string MainSceneName = "Main";
    private const string MenuSceneName = "Menu";
    private const string TutorialSceneName = "Tutorial";

    private const KeyCode ToggleKey = KeyCode.O;
    private const KeyCode PanelKey = KeyCode.F8;

    public override void OnInitializeMelon()
    {
        Settings.Load();
        if (!VerifyRequiredSymbols()) return;
        try
        {
            HarmonyInstance.PatchAll(typeof(Mod).Assembly);
        }
        catch (Exception ex)
        {
            ModState.MarkLoadFailed();
            LoggerInstance.Error(
                $"AutoAcceptDeals disabled: Harmony PatchAll failed ({ex.GetType().Name}: {ex.Message}). " +
                $"Likely incompatible Schedule I version. Expected {ExpectedVersion}.");
            return;
        }
        LoggerInstance.Msg("AutoAcceptDeals loaded — enabled. Press O in-game to toggle, F8 to open settings.");
    }

    private const string ExpectedVersion = "v0.4.6f11 (MelonLoader 0.7.3)";

    private bool VerifyRequiredSymbols()
    {
        var checks = new (Type type, string member, bool isProperty, Type[]? paramTypes)[]
        {
            (typeof(Customer), nameof(Customer.OfferContract), false, null),
            (typeof(Customer), nameof(Customer.SendCounteroffer), false, null),
            (typeof(Customer), nameof(Customer.PlayerAcceptedContract), false, null),
            (typeof(Customer), nameof(Customer.OfferedContractInfo), true, null),
            (typeof(Customer), nameof(Customer.CurrentContract), true, null),
            // nameof not usable — these members aren't referenced directly in this assembly
            (typeof(Map), "GetRegionFromPosition", false, null),
            (typeof(DealWindowInfo), "GetWindowInfo", false, null),
            (typeof(MessagingManager), "GetConversation", false, null),
            // Signature-sensitive: SettingsPanel calls these with a bool argument specifically.
            (typeof(PlayerCamera), "LockMouse", false, new[] { typeof(bool) }),
            (typeof(PlayerCamera), "FreeMouse", false, new[] { typeof(bool) }),
        };

        var failures = new List<string>();
        foreach (var (type, member, isProperty, paramTypes) in checks)
        {
            var found = isProperty
                ? (object?)AccessTools.PropertyGetter(type, member)
                : AccessTools.Method(type, member, paramTypes);
            if (found == null) failures.Add($"{type.Name}.{member}");
        }
        if (failures.Count > 0)
        {
            ModState.MarkLoadFailed();
            LoggerInstance.Error(
                $"AutoAcceptDeals disabled: missing game symbols: {string.Join(", ", failures)} — " +
                $"likely incompatible Schedule I version. Expected {ExpectedVersion}.");
            return false;
        }
        return true;
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
            SettingsPanel.ForceClose();
            DealListener.OnSceneLeave();
            if (!ModState.LeaveScene()) return;
            LoggerInstance.Msg("Left game scene; mod paused.");
        }
    }

    public override void OnUpdate()
    {
        if (!ModState.InGameScene) return;

        // Intermittent freeze observed on O-toggle; if a log line appears here the cause is
        // managed. If the freeze recurs with no log entry, the cause is native-side (likely
        // a UI hotkey conflict or EventSystem stall) — capture a mono_dump and inspect.
        try
        {
            if (IsTextInputFocused()) return;

            if (Input.GetKeyDown(PanelKey))
            {
                SettingsPanel.Toggle();
                return;
            }

            if (SettingsPanel.IsOpen) return;

            if (Input.GetKeyDown(ToggleKey))
            {
                ModState.Toggle();
                LoggerInstance.Msg($"AutoAcceptDeals toggled: {(ModState.Enabled ? "ON" : "OFF")}");
            }
        }
        catch (System.Exception ex)
        {
            LoggerInstance.Error($"AAD: OnUpdate input handling threw: {ex}");
        }
    }

    public override void OnGUI()
    {
        SettingsPanel.Draw();
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

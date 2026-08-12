using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.GameTime;
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

    private enum SymbolKind { Method, PropertyGetter, PropertySetter }

    private bool VerifyRequiredSymbols()
    {
        var checks = new (Type type, string member, SymbolKind kind, Type[]? paramTypes)[]
        {
            (typeof(Customer), nameof(Customer.OfferContract), SymbolKind.Method, null),
            (typeof(Customer), nameof(Customer.SendCounteroffer), SymbolKind.Method, null),
            (typeof(Customer), nameof(Customer.PlayerAcceptedContract), SymbolKind.Method, null),
            (typeof(Customer), nameof(Customer.ContractRejected), SymbolKind.Method, null),
            (typeof(Customer), nameof(Customer.OfferedContractInfo), SymbolKind.PropertyGetter, null),
            // The interop dummy exposes this setter as public specifically so DealListener can null
            // it out to defuse the offer-expiry timer — PropertyGetter alone wouldn't catch the
            // setter disappearing on a game update.
            (typeof(Customer), nameof(Customer.OfferedContractInfo), SymbolKind.PropertySetter, null),
            (typeof(Customer), nameof(Customer.CurrentContract), SymbolKind.PropertyGetter, null),
            // nameof not usable — these members aren't referenced directly in this assembly
            (typeof(Map), "GetRegionFromPosition", SymbolKind.Method, null),
            (typeof(DealWindowInfo), "GetWindowInfo", SymbolKind.Method, null),
            (typeof(MessagingManager), "GetConversation", SymbolKind.Method, null),
            (typeof(MSGConversation), nameof(MSGConversation.ResponseChosen), SymbolKind.Method, null),
            (typeof(MSGConversation), "currentResponses", SymbolKind.PropertyGetter, null),
            // WakeTime is `public const int WakeTime = 700` in-game — a const is inlined at compile
            // time, not backed by a property, so AccessTools.PropertyGetter can't resolve it (and
            // even if it could, a const can never be runtime-verified against the game's value).
            // CurrentTime/ElapsedDays below are real instance properties and are what actually guard
            // DealStats' day-rollover logic; that's sufficient.
            (typeof(TimeManager), nameof(TimeManager.CurrentTime), SymbolKind.PropertyGetter, null),
            (typeof(TimeManager), nameof(TimeManager.ElapsedDays), SymbolKind.PropertyGetter, null),
            // Signature-sensitive: SettingsPanel calls these with a bool argument specifically.
            (typeof(PlayerCamera), "LockMouse", SymbolKind.Method, new[] { typeof(bool) }),
            (typeof(PlayerCamera), "FreeMouse", SymbolKind.Method, new[] { typeof(bool) }),
            (typeof(PlayerCamera), nameof(PlayerCamera.CanLook), SymbolKind.PropertyGetter, null),
        };

        var failures = new List<string>();
        foreach (var (type, member, kind, paramTypes) in checks)
        {
            object? found = kind switch
            {
                SymbolKind.Method => AccessTools.Method(type, member, paramTypes),
                SymbolKind.PropertyGetter => AccessTools.PropertyGetter(type, member),
                SymbolKind.PropertySetter => AccessTools.PropertySetter(type, member),
                _ => null,
            };
            var suffix = kind == SymbolKind.PropertySetter ? " (setter)" : "";
            if (found == null) failures.Add($"{type.Name}.{member}{suffix}");
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
            // Inside the try: TimeManager.instance can still throw during a scene-transition
            // teardown even when InstanceExists reads true, and this handler exists precisely to
            // catch and log that once instead of letting it escape MelonMod.OnUpdate and spam the
            // log every frame until the transition finishes.
            DealStats.EnsureCurrentDay();

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

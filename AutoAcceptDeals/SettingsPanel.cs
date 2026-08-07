using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;

namespace AutoAcceptDeals;

/// <summary>
/// IMGUI settings panel. Static, single-threaded — all members must be called from the Unity main thread
/// (Mod.OnUpdate / Mod.OnGUI / scene callbacks). Schedule 1's IL2CPP build strips Unity's IMGUI text-input
/// and toggle code paths (NotSupportedException: Method unstripping failed on GUI.DoTextField), so this
/// panel is built from Box/Label/Button only — every numeric value is adjusted via increment buttons,
/// every mode picker is a row of plain Buttons with a "●" prefix on the active one. Edits go through
/// Settings.Set* immediately on each click.
/// </summary>
internal static class SettingsPanel
{
    private const string UIElementName = "AutoAcceptDeals.SettingsPanel";

    private static readonly EMapRegion[] _regions = Enum.GetValues<EMapRegion>();

    public static bool IsOpen { get; private set; }

    private static Rect _windowRect = new(40f, 40f, 480f, 600f);
    private static Vector2 _scroll;

    private static bool _suppressedCamera;
    private static bool _suppressedMovement;
    private static bool _suppressedInventory;
    private static bool _savedCanLook;
    private static bool _savedCanMove;
    private static bool _savedEquippingEnabled;

    // Set by the × button or other in-layout close paths; consumed in Draw() *after* GUILayout.EndArea
    // so we don't tear down a Begin/End pair mid-Repaint. OnGUI fires for both Layout and Repaint events
    // on the same click, so this flag is set twice — idempotent, the second pass no-ops.
    private static bool _pendingClose;

    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public static void Open()
    {
        if (IsOpen) return;
        var cam = GetCamera();
        if (cam != null)
        {
            _savedCanLook = cam.CanLook;
            cam.AddActiveUIElement(UIElementName);
            cam.SetCanLook(false);
            cam.FreeMouse(true);
            _suppressedCamera = true;
        }
        var move = GetMovement();
        if (move != null)
        {
            _savedCanMove = move.CanMove;
            move.CanMove = false;
            _suppressedMovement = true;
        }
        var inv = GetInventory();
        if (inv != null)
        {
            _savedEquippingEnabled = inv.EquippingEnabled;
            inv.SetEquippingEnabled(false);
            _suppressedInventory = true;
        }
        IsOpen = true;
    }

    public static void Close()
    {
        if (!IsOpen) return;
        if (_suppressedCamera)
        {
            var cam = GetCamera();
            if (cam != null)
            {
                cam.RemoveActiveUIElement(UIElementName);
                cam.SetCanLook(_savedCanLook);
                if (_savedCanLook) cam.LockMouse(true);
            }
            _suppressedCamera = false;
        }
        if (_suppressedMovement)
        {
            var move = GetMovement();
            if (move != null) move.CanMove = _savedCanMove;
            _suppressedMovement = false;
        }
        if (_suppressedInventory)
        {
            var inv = GetInventory();
            if (inv != null) inv.SetEquippingEnabled(_savedEquippingEnabled);
            _suppressedInventory = false;
        }
        IsOpen = false;
    }

    private static PlayerCamera? GetCamera()
    {
        try { return PlayerCamera.InstanceExists ? PlayerCamera.instance : null; }
        catch { return null; }
    }

    private static PlayerMovement? GetMovement()
    {
        try { return PlayerMovement.InstanceExists ? PlayerMovement.instance : null; }
        catch { return null; }
    }

    private static PlayerInventory? GetInventory()
    {
        try { return PlayerInventory.InstanceExists ? PlayerInventory.instance : null; }
        catch { return null; }
    }

    public static void ForceClose() => Close();

    public static void Draw()
    {
        if (!IsOpen) return;

        GUI.Box(_windowRect, "AutoAcceptDeals — Settings");
        var inner = new Rect(_windowRect.x + 8f, _windowRect.y + 24f,
                             _windowRect.width - 16f, _windowRect.height - 32f);
        GUILayout.BeginArea(inner);
        DrawBody();
        GUILayout.EndArea();

        // Without GUILayout.Window we have to claim mouse events manually so clicks/scrolls
        // on the panel don't reach world-space UI sitting behind it. Buttons inside the panel
        // already consume their own events; this catches the gaps (the box background, scrollview
        // gutter, label rows, drags, scrolls).
        ConsumeMouseEventsInRect(_windowRect);

        if (_pendingClose)
        {
            _pendingClose = false;
            Close();
        }
    }

    private static void ConsumeMouseEventsInRect(Rect r)
    {
        var ev = Event.current;
        if (ev == null) return;
        switch (ev.type)
        {
            case EventType.MouseDown:
            case EventType.MouseUp:
            case EventType.MouseDrag:
            case EventType.ScrollWheel:
                if (r.Contains(ev.mousePosition)) ev.Use();
                break;
        }
    }

    private static void DrawBody()
    {
        var scrollbarWidth = GUI.skin?.verticalScrollbar?.fixedWidth ?? 0f;
        if (scrollbarWidth <= 0f) scrollbarWidth = WrapScrollbarWidthFallback;
        _wrapMaxRowWidth = Mathf.Max(
            80f,
            _windowRect.width - WrapInnerPadding - scrollbarWidth - WrapIndent - WrapSafetyMargin);

        GUILayout.BeginHorizontal();
        GUILayout.Label($"Mod: {(ModState.Enabled ? "ON" : "OFF")}    (toggle with O)");
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("×", GUILayout.Width(28f), GUILayout.Height(20f)))
            _pendingClose = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        _scroll = GUILayout.BeginScrollView(_scroll);

        DrawRoundingSection();
        GUILayout.Space(8f);
        DrawProfitSection();
        GUILayout.Space(8f);
        DrawTimeSection();
        GUILayout.Space(8f);
        DrawLocationSection();

        GUILayout.EndScrollView();
    }

    private static void DrawRoundingSection()
    {
        GUILayout.Label($"Rounding multiple: {Settings.RoundingMultiple}   (0 = disabled)");
        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        if (GUILayout.Button("-5", GUILayout.Width(40f))) AdjustRounding(-5);
        if (GUILayout.Button("-1", GUILayout.Width(40f))) AdjustRounding(-1);
        if (GUILayout.Button("+1", GUILayout.Width(40f))) AdjustRounding(+1);
        if (GUILayout.Button("+5", GUILayout.Width(40f))) AdjustRounding(+5);
        GUILayout.Space(8f);
        if (GUILayout.Button("Reset to 0", GUILayout.Width(90f)))
        {
            if (Settings.RoundingMultiple != 0) Settings.SetRoundingMultiple(0);
        }
        GUILayout.EndHorizontal();
    }

    private static void DrawProfitSection()
    {
        GUILayout.Label($"Min profit: {Settings.MinProfitPercent:F0}%   (required per-unit price increase over the customer's ask; decline instead of countering below this)");
        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        if (GUILayout.Button("-5", GUILayout.Width(40f))) AdjustMinProfit(-5f);
        if (GUILayout.Button("-1", GUILayout.Width(40f))) AdjustMinProfit(-1f);
        if (GUILayout.Button("+1", GUILayout.Width(40f))) AdjustMinProfit(+1f);
        if (GUILayout.Button("+5", GUILayout.Width(40f))) AdjustMinProfit(+5f);
        GUILayout.EndHorizontal();

        GUILayout.Label($"Spending limit safety margin: {Settings.SpendingLimitSafetyMarginPercent:F0}%   " +
                        "(we estimate what a customer can afford, then only propose prices within this % of " +
                        "that estimate — lower values leave more buffer in case the estimate runs high)");
        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        if (GUILayout.Button("-5", GUILayout.Width(40f))) AdjustSafetyMargin(-5f);
        if (GUILayout.Button("-1", GUILayout.Width(40f))) AdjustSafetyMargin(-1f);
        if (GUILayout.Button("+1", GUILayout.Width(40f))) AdjustSafetyMargin(+1f);
        if (GUILayout.Button("+5", GUILayout.Width(40f))) AdjustSafetyMargin(+5f);
        GUILayout.EndHorizontal();
    }

    private static void DrawLocationSection()
    {
        GUILayout.Label("Delivery location");
        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        DrawLocationModeButton("Global",     LocationMode.Global);
        DrawLocationModeButton("Per region", LocationMode.PerRegion);
        GUILayout.EndHorizontal();

        if (Settings.LocationMode == LocationMode.Global)
            DrawGlobalLocationList();
        else
            DrawPerRegionLocationLists();
    }

    private static void DrawTimeSection()
    {
        GUILayout.Label("Delivery time");
        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        DrawTimeModeButton("Fixed",           TimeMode.Fixed);
        DrawTimeModeButton("Randomize",       TimeMode.Randomize);
        DrawTimeModeButton("Wait for player", TimeMode.WaitForPlayer);
        GUILayout.EndHorizontal();

        if (Settings.TimeMode == TimeMode.Fixed)
            DrawFixedTimeRow();
        else if (Settings.TimeMode == TimeMode.Randomize)
            GUILayout.Label("  (picks a random window per deal)");
    }

    private static void DrawLocationModeButton(string label, LocationMode mode)
    {
        bool currently = Settings.LocationMode == mode;
        var display = currently ? $"● {label}" : label;
        if (GUILayout.Button(display, GUILayout.ExpandWidth(false)))
        {
            if (!currently) Settings.SetLocationMode(mode);
        }
    }

    private static void DrawTimeModeButton(string label, TimeMode mode)
    {
        bool currently = Settings.TimeMode == mode;
        var display = currently ? $"● {label}" : label;
        if (GUILayout.Button(display, GUILayout.ExpandWidth(false)))
        {
            if (!currently) Settings.SetTimeMode(mode);
        }
    }

    private static void DrawGlobalLocationList()
    {
        var selectedGuid = Settings.GlobalLocationGuid;
        var selectedName = ResolveLocationName(selectedGuid);

        GUILayout.Label($"  Selected: {selectedName ?? "(None)"}");

        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        if (GUILayout.Button("(None)", GUILayout.ExpandWidth(false)))
        {
            if (Settings.GlobalLocationGuid != null) Settings.SetGlobalLocationGuid(null);
        }
        GUILayout.EndHorizontal();

        bool anyDiscovered = false;
        foreach (var region in _regions)
        {
            var locs = LocationsFor(region);
            if (locs.Count == 0) continue;
            anyDiscovered = true;
            GUILayout.Label($"  -- {region} --");
            DrawWrappedButtons(locs.Select(loc =>
            {
                var captured = loc;
                var label = captured.Guid == selectedGuid ? $"● {captured.Name}" : captured.Name;
                Action click = () => Settings.SetGlobalLocationGuid(captured.Guid);
                return (label, click);
            }));
        }

        if (!anyDiscovered)
            DrawDiscoveryHint("  (no locations discovered yet — receive a customer text first)");
    }

    private static void DrawPerRegionLocationLists()
    {
        foreach (var region in _regions)
        {
            Settings.RegionLocations.TryGetValue(region, out var selectedGuid);
            var locs = LocationsFor(region);
            var selectedName = locs.FirstOrDefault(l => l.Guid == selectedGuid)?.Name ?? "(None)";

            GUILayout.Label($"  {region}: {selectedName}");

            if (locs.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(20f);
                if (GUILayout.Button("(None)", GUILayout.ExpandWidth(false)))
                {
                    if (selectedGuid != null) Settings.SetRegionLocation(region, null);
                }
                DrawDiscoveryHint("(no locations discovered yet)");
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                continue;
            }

            var buttons = new List<(string, Action)>
            {
                ("(None)", () => { if (selectedGuid != null) Settings.SetRegionLocation(region, null); })
            };
            foreach (var loc in locs)
            {
                var captured = loc;
                var label = captured.Guid == selectedGuid ? $"● {captured.Name}" : captured.Name;
                buttons.Add((label, () => Settings.SetRegionLocation(region, captured.Guid)));
            }
            DrawWrappedButtons(buttons);
        }
    }

    private const float WrapButtonSpacing = 4f;
    private const float WrapIndent = 20f;
    private const float WrapInnerPadding = 16f;       // BeginArea horizontal padding (8 each side)
    private const float WrapScrollbarWidthFallback = 16f; // used only if GUI.skin.verticalScrollbar is unavailable
    private const float WrapSafetyMargin = 8f;        // keep the last button off the scrollbar

    // Recomputed each frame in DrawBody so the wrap adapts if _windowRect.width changes.
    private static float _wrapMaxRowWidth = 0f;

    private static void DrawWrappedButtons(IEnumerable<(string label, Action onClick)> buttons)
    {
        var style = GUI.skin.button;
        float used = 0f;
        bool rowOpen = false;
        foreach (var (label, onClick) in buttons)
        {
            var content = new GUIContent(label);
            var w = style.CalcSize(content).x + WrapButtonSpacing;
            if (!rowOpen)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(WrapIndent);
                rowOpen = true;
                used = 0f;
            }
            else if (used + w > _wrapMaxRowWidth)
            {
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Space(WrapIndent);
                used = 0f;
            }
            if (GUILayout.Button(content, GUILayout.ExpandWidth(false)))
                onClick();
            used += w;
        }
        if (rowOpen)
        {
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }

    private static void DrawFixedTimeRow()
    {
        GUILayout.Label($"  Window: {Settings.FixedWindow}");
        GUILayout.BeginHorizontal();
        GUILayout.Space(20f);
        DrawFixedWindowButton("Morning",   EDealWindow.Morning);
        DrawFixedWindowButton("Afternoon", EDealWindow.Afternoon);
        DrawFixedWindowButton("Night",     EDealWindow.Night);
        DrawFixedWindowButton("LateNight", EDealWindow.LateNight);
        GUILayout.EndHorizontal();
    }

    private static void DrawFixedWindowButton(string label, EDealWindow window)
    {
        bool currently = Settings.FixedWindow == window;
        var display = currently ? $"● {label}" : label;
        if (GUILayout.Button(display, GUILayout.ExpandWidth(false)))
        {
            if (!currently) Settings.SetFixedWindow(window);
        }
    }

    private static void AdjustRounding(int delta)
    {
        var v = Math.Max(0, Settings.RoundingMultiple + delta);
        if (v != Settings.RoundingMultiple) Settings.SetRoundingMultiple(v);
    }

    private static void AdjustMinProfit(float delta)
    {
        // Settings.SetMinProfitPercent requires > -100 (see its comment); clamp just above that
        // so repeated -5/-1 clicks can't throw.
        var v = Math.Max(-99f, Settings.MinProfitPercent + delta);
        if (v != Settings.MinProfitPercent) Settings.SetMinProfitPercent(v);
    }

    private static void AdjustSafetyMargin(float delta)
    {
        var v = Mathf.Clamp(Settings.SpendingLimitSafetyMarginPercent + delta, 1f, 100f);
        if (v != Settings.SpendingLimitSafetyMarginPercent) Settings.SetSpendingLimitSafetyMarginPercent(v);
    }

    private static void DrawDiscoveryHint(string text)
    {
        var prev = GUI.color;
        GUI.color = Color.gray;
        GUILayout.Label(text);
        GUI.color = prev;
    }

    private static IReadOnlyList<DiscoveredLocation> LocationsFor(EMapRegion region) =>
        Settings.DiscoveredLocations.TryGetValue(region, out var l) ? l : Array.Empty<DiscoveredLocation>();

    private static string? ResolveLocationName(string? guid)
    {
        if (guid == null) return null;
        foreach (var kvp in Settings.DiscoveredLocations)
        {
            var match = kvp.Value.FirstOrDefault(l => l.Guid == guid);
            if (match != null) return match.Name;
        }
        return null;
    }
}

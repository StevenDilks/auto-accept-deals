using Il2CppScheduleOne.GameTime;
using MelonLoader;

namespace AutoAcceptDeals;

/// <summary>
/// Static, single-threaded, in-memory only (not persisted across restarts). All members must be
/// called from the Unity main thread.
/// </summary>
internal static class DealStats
{
    // -1 = never checked yet; forces the first EnsureCurrentDay call to seed rather than "reset".
    private static int _lastElapsedDays = -1;
    private static bool _pendingWakeReset;

    private static float _marginSum;
    private static int _marginCount;

    public static int DealsMade { get; private set; }
    public static int DealsDeclined { get; private set; }
    public static float AverageProfitMarginPercent => _marginCount > 0 ? _marginSum / _marginCount : 0f;

    public static float? SuccessRatePercent
    {
        get
        {
            var total = DealsMade + DealsDeclined;
            return total > 0 ? DealsMade / (float)total * 100f : null;
        }
    }

    public static void RecordDealMade(float profitMarginPercent)
    {
        EnsureCurrentDay();
        DealsMade++;
        _marginSum += profitMarginPercent;
        _marginCount++;
    }

    public static void RecordDealDeclined()
    {
        EnsureCurrentDay();
        DealsDeclined++;
    }

    // Forces the next EnsureCurrentDay call to reseed rather than compare against a stale day
    // number left over from a previous save (e.g. loading a different save with a lower ElapsedDays).
    public static void ResetForSceneLeave()
    {
        _lastElapsedDays = -1;
        _pendingWakeReset = false;
    }

    // Cheap poll instead of subscribing to TimeManager.onDayPass — avoids subscribe/unsubscribe
    // bookkeeping across scene loads. Called on every stat write, every settings-panel redraw, and
    // every frame from Mod.OnUpdate (so the reset lands close to real-time instead of waiting for
    // the next incidental call).
    //
    // ElapsedDays alone isn't the right reset trigger: it bumps whenever the player's sleep happens
    // to complete, which can be any real moment (observed: as late as noon) — not a fixed clock
    // time. The in-game day actually runs until 4 AM, so the 00:00-06:00 stretch still belongs to
    // the day that's ending; the new business day only starts once the clock reaches WakeTime
    // (6 AM). So: flag a pending reset the moment ElapsedDays changes, then only apply it once
    // CurrentTime has actually reached WakeTime.
    public static void EnsureCurrentDay()
    {
        if (!TimeManager.InstanceExists) return;
        var tm = TimeManager.instance;
        var elapsed = tm.ElapsedDays;

        if (_lastElapsedDays == -1)
        {
            _lastElapsedDays = elapsed;
            return;
        }

        if (elapsed != _lastElapsedDays)
        {
            _lastElapsedDays = elapsed;
            _pendingWakeReset = true;
        }

        if (!_pendingWakeReset || tm.CurrentTime < TimeManager.WakeTime) return;
        _pendingWakeReset = false;

        MelonLogger.Msg(
            $"AAD: new in-game day — resetting daily stats ({DealsMade} made, {DealsDeclined} declined, " +
            $"{AverageProfitMarginPercent:F1}% avg margin).");

        DealsMade = 0;
        DealsDeclined = 0;
        _marginSum = 0f;
        _marginCount = 0;
    }
}

using System;

namespace AutoAcceptDeals;

// Direct port of BetterCounterOfferUI 3.3.0 (OverweightUnicorn) CalculateSuccessProbability math.
// Fully deterministic — pure arithmetic; no RNG, no game-API calls after the caller pre-computes vp2.
// Replaces the non-deterministic EvaluateCounteroffer binary search from Phase 6.
internal static class ProbabilityFormula
{
    // Clamps t to [0,1] before interpolating — matches UnityEngine.Mathf.Lerp behavior.
    private static float Lerp(float a, float b, float t)
    {
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        return a + (b - a) * t;
    }

    // Result clamped to [0,1] — matches UnityEngine.Mathf.InverseLerp behavior.
    private static float InverseLerp(float a, float b, float v)
    {
        if (a == b) return 0f;
        float t = (v - a) / (b - a);
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }

    /// <param name="price">Counter-offer total price being evaluated.</param>
    /// <param name="spendingLimit">Customer's max tolerable daily spend × 3 (hard 0% cut above this).</param>
    /// <param name="vp0">GetValueProposition(originalProduct, originalPayment / originalQty).</param>
    /// <param name="vp2">GetValueProposition(product, price / qty) — computed per price by caller.</param>
    /// <param name="productEnjoyment">customer.GetProductEnjoyment(product, customerQuality).</param>
    /// <param name="qty">Counter-offer quantity (after rounding).</param>
    /// <param name="origQty">Original offered quantity (before rounding).</param>
    /// <param name="maxAddictionRelation">max(CurrentAddiction, NormalizedRelationDelta).</param>
    /// <returns>Acceptance probability in [0, 1].</returns>
    public static float Compute(
        float price,
        float spendingLimit,
        float vp0,
        float vp2,
        float productEnjoyment,
        int qty,
        int origQty,
        float maxAddictionRelation)
    {
        if (price >= spendingLimit) return 0f;

        float num3 = MathF.Pow((float)qty / MathF.Max(1f, origQty), 0.6f);
        float num4 = Lerp(0f, 2f, num3 * 0.5f);
        float num5 = Lerp(1f, 0f, MathF.Abs(num4 - 1f));

        if (vp2 * num5 > vp0) return 1f;
        if (vp2 < 0.12f) return 0f;

        float num2 = InverseLerp(-1f, 1f, productEnjoyment);
        // Faithful port: BetterCounterOfferUI uses raw productEnjoyment here (not the clamped num2).
        // When productEnjoyment < 0, num6 < 0 while num7 ≥ 0, so num7 > num6 always fires → returns 1f.
        float num6 = productEnjoyment * vp0;
        float num7 = num2 * num5 * vp2;
        if (num7 > num6) return 1f;

        float num8 = num6 - num7;
        float num9 = Lerp(0f, 1f, num8 / 0.2f);
        float num11 = Lerp(0f, 0.2f, maxAddictionRelation);

        if (num9 <= num11) return 1f;
        if (num9 - num11 >= 0.9f) return 0f;

        return MathF.Max(0f, MathF.Min(1f, (0.9f + num11 - num9) / 0.9f));
    }

    // True once qty/origQty is large enough that num5 (the quantity-ratio term above) is exactly
    // zero — the point past which Compute stops depending on qty at all, no matter how much
    // farther qty climbs (num5 only decreases from here; it never recovers). Mirrors num3/num4/num5
    // verbatim instead of a separately-derived closed-form ratio threshold, so it can never drift
    // from Compute if that math changes.
    internal static bool IsDeadZoneQty(int qty, int origQty)
    {
        float num3 = MathF.Pow((float)qty / MathF.Max(1f, origQty), 0.6f);
        float num4 = Lerp(0f, 2f, num3 * 0.5f);
        float num5 = Lerp(1f, 0f, MathF.Abs(num4 - 1f));
        return num5 == 0f;
    }

    // True iff, once IsDeadZoneQty holds, Compute cannot return >= 1f for *any* vp2 — i.e. the
    // accept/reject verdict has stopped depending on price entirely, not just on the qty-ratio term.
    // Setting num5 = 0 in Compute and tracing every remaining gate:
    //   - `vp2 * num5 > vp0` becomes `0 > vp0`, which never fires (GetValueProposition is never
    //     negative — the same assumption the num6/num7 comment on the negative-enjoyment gate above
    //     already relies on).
    //   - num7 = num2 * num5 * vp2 collapses to 0 regardless of vp2, so `num7 > num6` becomes
    //     `productEnjoyment * vp0 < 0`, independent of vp2 — if true, Compute returns 1f for any vp2
    //     that clears the vp2 < 0.12 pre-gate, so it is NOT an always-reject case.
    //   - num8/num9/num11 inherit that same vp2-independence, so `num9 <= num11` is likewise a
    //     constant test — if true, same story, not an always-reject case.
    // Only when none of those three fires does the tail never reach 1f, for every vp2 that could
    // ever pass the one gate that still depends on it — genuinely unconditional on price at this qty.
    internal static bool DeadZoneAlwaysRejects(float vp0, float productEnjoyment, float maxAddictionRelation)
    {
        if (vp0 < 0f) return false;

        float num6 = productEnjoyment * vp0;
        if (num6 < 0f) return false;

        float num9 = Lerp(0f, 1f, num6 / 0.2f);
        float num11 = Lerp(0f, 0.2f, maxAddictionRelation);
        return num9 > num11;
    }

    // Smallest qty in [1, cap] where IsDeadZoneQty holds, or cap+1 if none does. IsDeadZoneQty is
    // monotone non-decreasing in qty for qty >= 1 (num5 only decreases past the point it first
    // hits zero), so bisection over that range is exact. qty == 0 is excluded from the claim, not
    // covered by it: num4 = clamp(num3, 0, 2) is 1 away from 1 at both num3 == 0 (qty == 0) and
    // num3 >= 2, so IsDeadZoneQty(0, origQty) is also true — a tent, not a step — which is exactly
    // why lo starts at 1 rather than 0. Callers can use this to cap a climb before it ever enters
    // the dead zone, instead of walking into it one candidate at a time and discovering the
    // boundary the hard way — it still only ever asks IsDeadZoneQty, never derives the ratio
    // threshold itself (PR #14 review, round 9; monotonicity clarified round 10).
    internal static int FirstDeadZoneQty(int origQty, int cap)
    {
        if (!IsDeadZoneQty(cap, origQty)) return cap + 1;

        int lo = 1, hi = cap;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (IsDeadZoneQty(mid, origQty)) hi = mid; else lo = mid + 1;
        }
        return lo;
    }
}

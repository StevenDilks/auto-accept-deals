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
}

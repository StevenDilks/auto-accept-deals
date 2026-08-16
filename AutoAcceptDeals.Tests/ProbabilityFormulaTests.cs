using Xunit;

namespace AutoAcceptDeals.Tests;

public class ProbabilityFormulaTests
{
    // qty == origQty throughout most tests → num3=1, num4=1, num5=1 (no quantity-ratio effect).
    private const int Q = 25;

    // --- Hard cuts ---

    [Fact]
    public void SpendingLimitCut_PriceAtLimit_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(1000f, 1000f, 0.5f, 0.5f, 0f, Q, Q, 0f));

    [Fact]
    public void SpendingLimitCut_PriceAboveLimit_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(1001f, 1000f, 0.5f, 0.5f, 0f, Q, Q, 0f));

    // vp2 * num5 (=1) > vp0 → immediate 1f
    [Fact]
    public void TopEndCut_Vp2ExceedsVp0_Returns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.9f, 0f, Q, Q, 0f));

    // vp2 (0.11) < 0.12 — falls through the vp2*num5 > vp0 check (0.11 < 0.5), then hits vp2<0.12 cut
    [Fact]
    public void BottomEndCut_Vp2BelowThreshold_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.11f, 0f, Q, Q, 0f));

    // Negative enjoyment: num6 = productEnjoyment * vp0 < 0; num7 = num2 * num5 * vp2 ≥ 0 → num7 > num6 always → 1f.
    // Faithful port of BetterCounterOfferUI; the guard is intentionally bypassed for disliked products.
    [Fact]
    public void NegativeEnjoyment_AlwaysReturns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.4f, productEnjoyment: -0.5f, Q, Q, 0f));

    // High enjoyment pushes num7 above num6 → 1f
    // enjoyment=1 → num2=1; vp2=0.4, vp0=0.3: vp2*num5=0.4>0.3 fires the top-end cut first.
    // Use enjoyment=0.5 (num2=0.75), vp2=0.4, vp0=0.5:
    //   vp2*num5=0.4 ≤ 0.5; vp2≥0.12; num6=0.5*0.5=0.25; num7=0.75*1*0.4=0.30 > 0.25 → 1f
    [Fact]
    public void Num7GreaterThanNum6_Returns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.4f, productEnjoyment: 0.5f, Q, Q, 0f));

    // High addiction/relation raises num11 ≥ num9 → 1f
    // enjoyment=0.5, vp2=0.32, vp0=0.5, maxAddRel=0.5:
    //   num7=0.75*0.32=0.24 < num6=0.25 → continues; num8=0.01; num9=0.05; num11=0.1 ≥ num9 → 1f
    [Fact]
    public void Num9LessThanOrEqualNum11_Returns1()
        => Assert.Equal(1f, ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, Q, Q, maxAddictionRelation: 0.5f));

    // Large num8 (clamped num9 → 1) with maxAddRel=0 (num11=0): 1 - 0 = 1 ≥ 0.9 → 0f
    // enjoyment=0.5, vp2=0.3, vp0=1.0: num6=0.5; num7=0.75*0.3=0.225; num8=0.275; num9=1 (clamped)
    [Fact]
    public void Num9MinusNum11ReachesThreshold_Returns0()
        => Assert.Equal(0f, ProbabilityFormula.Compute(500f, 1000f, vp0: 1.0f, vp2: 0.3f, productEnjoyment: 0.5f, Q, Q, maxAddictionRelation: 0f));

    // --- Continuous path ---

    // enjoyment=0.5, vp2=0.32, vp0=0.5, maxAddRel=0: reaches final Clamp → result ∈ (0,1)
    [Fact]
    public void ClampedLerpPath_ReturnsValueBetween0And1()
    {
        float result = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, Q, Q, maxAddictionRelation: 0f);
        Assert.True(result > 0f && result < 1f, $"Expected (0,1) exclusive, got {result}");
    }

    // --- Boundary sanity ---

    [Fact]
    public void PriceJustBelowSpendingLimit_DoesNotReturn0FromCut()
    {
        // price = 999, limit = 1000 → price < limit → does not return 0 from the spending-limit cut
        // (may return other values; just confirm it doesn't short-circuit to 0 from the limit check alone)
        float result = ProbabilityFormula.Compute(999f, 1000f, vp0: 0.5f, vp2: 0.9f, 0f, Q, Q, 0f);
        // vp2*num5=0.9>0.5 → returns 1f here; the point is the limit cut does NOT fire at 999
        Assert.Equal(1f, result);
    }

    // --- Quantity-ratio effect ---

    // Same price but doubled qty changes the probability.
    [Fact]
    public void QuantityRatio_DifferentQtyYieldsDifferentProbability()
    {
        // qty=25, origQty=25 (ratio=1)
        float p1 = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 25, origQty: 25, maxAddictionRelation: 0f);
        // qty=50, origQty=25 (ratio=2, reducing num5)
        float p2 = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 50, origQty: 25, maxAddictionRelation: 0f);
        Assert.NotEqual(p1, p2);
    }

    // num5 (the quantity-ratio term) is a tent in isolation: it peaks at qty == origQty and decays
    // as the ratio moves away in *either* direction, holding vp2 fixed. This does NOT by itself
    // show that overall acceptance is single-peaked in qty — vp2 is itself qty-dependent in the
    // real engine (Customer.GetValueProposition(product, price / qty) rises as qty rises at a
    // fixed price, the opposite direction from num5) and Compute's several early-exit branches add
    // further non-monotonicity on top of that. See QuantityAndValueProposition_NonMonotonic below
    // for why QuantitySearch.FindBest does not stop climbing at the first infeasible candidate.
    [Fact]
    public void QuantityRatio_IsATent_DecreasesOnBothSidesOfOrigQty()
    {
        const int origQty = 25;
        float pBelow = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 13, origQty, maxAddictionRelation: 0f);
        float pAt    = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: origQty, origQty, maxAddictionRelation: 0f);
        float pAbove = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.32f, productEnjoyment: 0.5f, qty: 50, origQty, maxAddictionRelation: 0f);

        Assert.True(pAt >= pBelow, $"Expected peak at origQty: pAt={pAt} should be >= pBelow={pBelow}");
        Assert.True(pAt >= pAbove, $"Expected peak at origQty: pAt={pAt} should be >= pAbove={pAbove}");
    }

    // Demonstrates the real non-monotonicity, with a much more conservative vp2 coupling than an
    // earlier version of this test used. In the engine, vp2 = Customer.GetValueProposition(product,
    // price/qty), so moving qty 28 -> 40 (+42.9%) at a fixed price lowers price/qty by ~28.6%; the
    // two values below move vp2 30 -> 43 (+43.3%), i.e. assume roughly unit elasticity between a
    // price/qty change and GetValueProposition's response — not the ~14.7x-elasticity, 367% jump
    // (vp2 0.15 -> 0.70 for a mere +25% qty change) the previous version of this test relied on,
    // which the engine's own coupling almost certainly can't produce.
    //
    // Rather than lean on the vp2*num5>vp0 shortcut again (which needs a large absolute vp2 to
    // begin with), this drives the crossing through the num9<=num11 branch cliff instead — per
    // review feedback, a hard threshold that only needs a modest vp2 delta to cross is a more
    // defensible source of non-monotonicity than asserting a specific vp2 magnitude.
    // maxAddictionRelation=0.9 is a high but valid value (it's a max() of two already-normalized
    // inputs), giving num11 = 0.18 for both calls.
    [Fact]
    public void QuantityAndValueProposition_NonMonotonic_NearerQtyCanBeInfeasibleWhileFartherQtyIsFeasible()
    {
        const int origQty = 25;

        // qty=28 is close to origQty (num5 ~0.930): num9 ~0.204, just above num11 (0.18) -> falls
        // through to the continuous path at p ~0.973 -- below the p>=1.0 threshold BisectBestPrice
        // requires, by a comfortable margin.
        float pNearer = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.30f, productEnjoyment: 0.5f, qty: 28, origQty, maxAddictionRelation: 0.9f);

        // qty=40 is farther from origQty (num5 ~0.674, lower than at qty=28): the higher vp2 pulls
        // num9 down to ~0.163, at or below num11 (0.18) -> hits the num9<=num11 shortcut and
        // returns 1f outright, with a comfortable margin on the threshold too.
        float pFarther = ProbabilityFormula.Compute(500f, 1000f, vp0: 0.5f, vp2: 0.43f, productEnjoyment: 0.5f, qty: 40, origQty, maxAddictionRelation: 0.9f);

        Assert.True(pNearer < 1f, $"Expected the nearer qty to be infeasible at this vp2, got {pNearer}");
        Assert.Equal(1f, pFarther);
    }

    // --- Dead zone (num5 == 0) ---

    // threshold ratio is 2^(1/0.6) ~= 3.1748; origQty=10 -> ceil(31.748) = 32.
    [Fact]
    public void IsDeadZoneQty_JustBelowThreshold_ReturnsFalse()
        => Assert.False(ProbabilityFormula.IsDeadZoneQty(qty: 31, origQty: 10));

    [Fact]
    public void IsDeadZoneQty_AtThreshold_ReturnsTrue()
        => Assert.True(ProbabilityFormula.IsDeadZoneQty(qty: 32, origQty: 10));

    [Fact]
    public void IsDeadZoneQty_FarAboveThreshold_StaysTrue()
        => Assert.True(ProbabilityFormula.IsDeadZoneQty(qty: 1000, origQty: 10));

    [Fact]
    public void IsDeadZoneQty_AtOrigQty_ReturnsFalse()
        => Assert.False(ProbabilityFormula.IsDeadZoneQty(qty: 10, origQty: 10));

    // vp0 < 0 means the vp2*num5 > vp0 gate (0 > vp0) always fires regardless of vp2 -> Compute
    // always returns 1f at a dead-zone qty. That's the opposite of always-reject, so the method
    // must say false here, not just "not provably true".
    [Fact]
    public void DeadZoneAlwaysRejects_NegativeVp0_ReturnsFalse()
        => Assert.False(ProbabilityFormula.DeadZoneAlwaysRejects(vp0: -0.1f, productEnjoyment: 0.5f, maxAddictionRelation: 0f));

    // productEnjoyment < 0 with vp0 >= 0 means num6 < 0, so num7 (== 0 here) > num6 always fires ->
    // Compute always returns 1f once vp2 clears the 0.12 pre-gate. Also not always-reject.
    [Fact]
    public void DeadZoneAlwaysRejects_NegativeEnjoyment_ReturnsFalse()
        => Assert.False(ProbabilityFormula.DeadZoneAlwaysRejects(vp0: 0.5f, productEnjoyment: -0.5f, maxAddictionRelation: 0f));

    // num6 small enough (or maxAddictionRelation high enough) that num9 <= num11 -> Compute always
    // returns 1f once vp2 clears the pre-gate. Also not always-reject.
    [Fact]
    public void DeadZoneAlwaysRejects_Num9AtOrBelowNum11_ReturnsFalse()
        => Assert.False(ProbabilityFormula.DeadZoneAlwaysRejects(vp0: 0.1f, productEnjoyment: 0.1f, maxAddictionRelation: 0.9f));

    // vp0 and productEnjoyment both high, no addiction/relation cushion -> num9 saturates to 1,
    // num11 = 0 -> the tail can never reach 1f for any vp2. Genuinely always-reject.
    [Fact]
    public void DeadZoneAlwaysRejects_HighEnjoymentAndVp0_NoAddictionCushion_ReturnsTrue()
        => Assert.True(ProbabilityFormula.DeadZoneAlwaysRejects(vp0: 1.0f, productEnjoyment: 1.0f, maxAddictionRelation: 0f));

    // Cross-check against Compute itself, not just against the hand-derived reduction above: when
    // DeadZoneAlwaysRejects is true, Compute must never reach 1f at a dead-zone qty, for a spread of
    // vp2 values on both sides of the 0.12 pre-gate and both sides of the spending-limit cut.
    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.11f)]
    [InlineData(0.12f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void DeadZoneAlwaysRejects_True_MatchesCompute_AcrossVp2Sweep(float vp2)
    {
        const int origQty = 10, qty = 1000; // qty far past IsDeadZoneQty's threshold for origQty=10
        Assert.True(ProbabilityFormula.IsDeadZoneQty(qty, origQty));
        Assert.True(ProbabilityFormula.DeadZoneAlwaysRejects(vp0: 1.0f, productEnjoyment: 1.0f, maxAddictionRelation: 0f));

        float p = ProbabilityFormula.Compute(
            price: 500f, spendingLimit: 1000f, vp0: 1.0f, vp2, productEnjoyment: 1.0f, qty, origQty, maxAddictionRelation: 0f);
        Assert.True(p < 1f, $"Expected < 1f at vp2={vp2}, got {p}");
    }

    // Mirror check in the other direction: when DeadZoneAlwaysRejects is false, Compute can still
    // reach 1f at a dead-zone qty for at least one vp2 — confirms the predicate isn't just
    // conservatively false everywhere.
    [Fact]
    public void DeadZoneAlwaysRejects_False_ComputeCanStillReach1AtDeadZoneQty()
    {
        const int origQty = 10, qty = 1000;
        Assert.True(ProbabilityFormula.IsDeadZoneQty(qty, origQty));
        Assert.False(ProbabilityFormula.DeadZoneAlwaysRejects(vp0: 0.5f, productEnjoyment: -0.5f, maxAddictionRelation: 0f));

        float p = ProbabilityFormula.Compute(
            price: 500f, spendingLimit: 1000f, vp0: 0.5f, vp2: 0.5f, productEnjoyment: -0.5f, qty, origQty, maxAddictionRelation: 0f);
        Assert.Equal(1f, p);
    }

    // --- FirstDeadZoneQty ---

    // Threshold for origQty=10 is 32 (see IsDeadZoneQty_AtThreshold_ReturnsTrue above); a cap well
    // past it should still land exactly on the threshold, not just "somewhere in the dead zone".
    [Fact]
    public void FirstDeadZoneQty_CapWellPastThreshold_ReturnsThreshold()
        => Assert.Equal(32, ProbabilityFormula.FirstDeadZoneQty(origQty: 10, cap: 1000));

    [Fact]
    public void FirstDeadZoneQty_CapExactlyAtThreshold_ReturnsCap()
        => Assert.Equal(32, ProbabilityFormula.FirstDeadZoneQty(origQty: 10, cap: 32));

    // The dead zone never opens within [1, cap] here, so there's no such qty to return — cap+1 is
    // the documented sentinel for "none", not a clamped/incorrect answer.
    [Fact]
    public void FirstDeadZoneQty_CapBelowThreshold_ReturnsCapPlusOne()
        => Assert.Equal(21, ProbabilityFormula.FirstDeadZoneQty(origQty: 10, cap: 20));

    [Fact]
    public void FirstDeadZoneQty_CapOneBelowThreshold_ReturnsCapPlusOne()
        => Assert.Equal(32, ProbabilityFormula.FirstDeadZoneQty(origQty: 10, cap: 31));

    // Cross-check against IsDeadZoneQty directly, across several origQty values, rather than
    // trusting one hand-derived boundary: the returned qty must itself be a dead-zone qty, and the
    // qty immediately below it must not be — the exact bisection invariant.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(117)]
    public void FirstDeadZoneQty_MatchesIsDeadZoneQty_BisectionInvariant(int origQty)
    {
        const int cap = 1000;
        int first = ProbabilityFormula.FirstDeadZoneQty(origQty, cap);
        Assert.True(first >= 1); // pins lo's floor: qty == 0 is also a dead-zone qty (tent, not a
                                  // step), so first - 1 below must stay >= 0 or IsDeadZoneQty sees a
                                  // negative qty and MathF.Pow yields NaN, silently passing Assert.False
        Assert.True(first <= cap);
        Assert.True(ProbabilityFormula.IsDeadZoneQty(first, origQty));
        Assert.False(ProbabilityFormula.IsDeadZoneQty(first - 1, origQty));
    }

    // --- Representative real-world cross-check ---

    // Mirroring the BetterCounterOfferUI probe values that drove the Phase 6 confirmation:
    // greencrack, qty=25, vp0 derived from $1290 total (=$51.60/unit).
    // At the customer's exact offer price the formula should give ≥ 0 (any valid probability).
    [Fact]
    public void ProbeAtOfferPrice_ReturnsValidProbability()
    {
        // Approximate inputs from the Phase 6 probe; exact vp0/vp2 will vary but the formula must not throw.
        float result = ProbabilityFormula.Compute(
            price: 1290f,
            spendingLimit: 4000f,
            vp0: 0.45f,
            vp2: 0.45f,
            productEnjoyment: 0.3f,
            qty: 25,
            origQty: 25,
            maxAddictionRelation: 0.1f);
        Assert.True(result >= 0f && result <= 1f, $"Expected [0,1], got {result}");
    }
}

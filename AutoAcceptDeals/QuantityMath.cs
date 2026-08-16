namespace AutoAcceptDeals;

internal static class QuantityMath
{
    // Customer.MaxOrderQuantityPerProduct. Lives here (not CounterOfferEngine, which references
    // Il2Cpp game types) so Settings can clamp RoundingMultiple against it without creating a
    // dependency from the engine-free config layer onto the game-facing engine layer, and so the
    // bound is reachable from the engine-free test project.
    internal const int QuantityCap = 1000;

    internal static int RoundUpToMultiple(int v, int multiple)
    {
        if (multiple <= 0) return v;
        return ((v + multiple - 1) / multiple) * multiple;
    }

    internal static int Clamp(int v, int max) => v <= max ? v : max;
}

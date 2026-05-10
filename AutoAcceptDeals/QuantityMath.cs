namespace AutoAcceptDeals;

internal static class QuantityMath
{
    internal static int RoundUpToMultiple(int v, int multiple)
    {
        if (multiple <= 0) return v;
        return ((v + multiple - 1) / multiple) * multiple;
    }

    internal static int Clamp(int v, int max) => v <= max ? v : max;
}

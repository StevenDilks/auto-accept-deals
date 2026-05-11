using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

internal sealed class PendingSendRegistry<T>
{
    private readonly Dictionary<IntPtr, T> _sends = new();

    public void Register(IntPtr key, T value) => _sends[key] = value;

    public bool HasPending(IntPtr key) => _sends.ContainsKey(key);

    public T? TakeForKey(IntPtr key)
    {
        if (!_sends.TryGetValue(key, out var v)) return default;
        _sends.Remove(key);
        return v;
    }

    public void Clear() => _sends.Clear();
}

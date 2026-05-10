using System;
using System.Collections.Generic;

namespace AutoAcceptDeals;

internal sealed class PendingSendRegistry<T>
{
    private readonly Dictionary<IntPtr, T> _sends = new();
    private readonly HashSet<IntPtr> _subscribed = new();

    public void Register(IntPtr key, T value) => _sends[key] = value;

    public bool HasPending(IntPtr key) => _sends.ContainsKey(key);

    public T? TakeForKey(IntPtr key)
    {
        if (!_sends.TryGetValue(key, out var v)) return default;
        _sends.Remove(key);
        return v;
    }

    public bool TrySubscribeOnce(IntPtr key) => _subscribed.Add(key);

    public void Clear()
    {
        _sends.Clear();
        _subscribed.Clear();
    }
}

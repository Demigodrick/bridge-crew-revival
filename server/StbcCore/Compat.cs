using System.Collections.Generic;

namespace StbcServer;

/// <summary>Small helpers the .NET 3.5 base library lacks.</summary>
public static class Compat
{
    public static TValue? GetOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> d, TKey key) =>
        d.TryGetValue(key, out var v) ? v : default;

    public static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public static int BytesHash(byte[] a)
    {
        unchecked
        {
            int h = (int)2166136261;
            foreach (var x in a) h = (h ^ x) * 16777619;
            return h;
        }
    }
}

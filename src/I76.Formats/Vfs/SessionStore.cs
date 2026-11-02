using System.Collections.Concurrent;

namespace I76.Formats.Vfs;

/// <summary>
/// What a build decodes from the install and keeps for the next build of the same session (the map editor's: its scene, its
/// playtests and the editor again all read the same files). Off outside a session; <see cref="Begin"/> opens one and <see cref="End"/>
/// drops everything. Values are read-only once stored. It holds at most <see cref="Budget"/> bytes (what the callers report): past
/// that it is emptied and starts over, so a long session on a big map cannot grow without bound.
/// </summary>
public static class SessionStore
{
    /// <summary>The most it holds, bytes.</summary>
    public const long Budget = 512L << 20;

    private static readonly ConcurrentDictionary<string, object?> Items = new(StringComparer.OrdinalIgnoreCase);
    private static long _bytes;
    private static volatile bool _on;

    public static bool Active => _on;
    public static long Bytes => Interlocked.Read(ref _bytes);
    public static int Count => Items.Count;

    public static void Begin() { Clear(); _on = true; }
    public static void End() { _on = false; Clear(); }

    public static void Clear()
    {
        Items.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }

    /// <summary>The value stored under <paramref name="key"/>, if a session is open and there is one.</summary>
    public static T? TryGet<T>(string key) where T : class => _on && Items.TryGetValue(key, out var v) ? v as T : null;

    /// <summary>Stores a value with its size (when a session is open).</summary>
    public static void Put(string key, object value, long size)
    {
        if (!_on) return;
        if (Interlocked.Add(ref _bytes, size + 64) > Budget) Clear();
        Items[key] = value;
    }

    /// <summary>The stored value for <paramref name="key"/>, else <paramref name="make"/>'s (stored with its size when a session is open). A null result is stored too (a name with no file).</summary>
    public static T? GetOrAdd<T>(string key, Func<T?> make, Func<T?, long> size) where T : class
    {
        if (!_on) return make();
        if (Items.TryGetValue(key, out var hit)) return (T?)hit;
        var made = make();
        if (Interlocked.Add(ref _bytes, size(made) + 64) > Budget) { Clear(); }
        Items[key] = made;
        return made;
    }
}

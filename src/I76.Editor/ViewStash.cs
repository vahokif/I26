namespace I76.Editor;

/// <summary>
/// One built view of a map kept aside while something else runs (the map editor's scene while its playtest plays), to be taken back
/// when nothing it shows has changed since: the stash remembers the document's <see cref="TerrainDocument.Revision"/> and the map it
/// was built for, and gives the view back only for the same pair. A view not taken back (an edit, another map, the editor left) is
/// disposed, never reused.
/// </summary>
public sealed class ViewStash<T> where T : class
{
    private T? _view;
    private string _key = "";
    private long _revision;
    private readonly Action<T> _dispose;

    public ViewStash(Action<T> dispose) => _dispose = dispose;

    public bool Holds => _view is not null;
    public T? View => _view;

    /// <summary>Keeps <paramref name="view"/> built from <paramref name="key"/> (the map and install) at <paramref name="revision"/>; one held before is disposed.</summary>
    public void Hold(T view, string key, long revision)
    {
        Drop();
        (_view, _key, _revision) = (view, key, revision);
    }

    /// <summary>The held view if it was built for this key at this revision (and it is no longer held), else null with the view disposed.</summary>
    public T? Take(string key, long revision)
    {
        var v = _view;
        if (v is null) return null;
        _view = null;
        if (_key == key && _revision == revision) return v;
        _dispose(v);
        return null;
    }

    public void Drop()
    {
        if (_view is { } v) { _view = null; _dispose(v); }
    }
}

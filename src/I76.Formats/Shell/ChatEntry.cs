namespace I76.Formats.Shell;

/// <summary>A key the chat line cares about; everything else is <see cref="Other"/> (and is typed, if it has a character).</summary>
public enum ChatKey { Other, Enter, Escape, Backspace }

/// <summary>What a key press did to the chat line.</summary>
public enum ChatResult { None, Sent, Cancelled }

/// <summary>
/// The one-line chat entry of a network game. While it is open it takes every key (so no game key reaches the car); Enter
/// sends the line unless it is empty, Esc drops it. Characters come from <see cref="TextEntry"/>, the shell's own handling.
/// </summary>
public sealed class ChatEntry
{
    private readonly System.Text.StringBuilder _text = new();
    public int MaxLength { get; init; } = 100;
    public bool Active { get; private set; }
    public string Text => _text.ToString();
    /// <summary>The line the last <see cref="ChatResult.Sent"/> press sent.</summary>
    public string? Sent { get; private set; }

    public void Open() { Active = true; _text.Clear(); Sent = null; }

    /// <summary>A key press (repeats too) while the entry is open; the key is always consumed then (<paramref name="typed"/> is TextEntry.Typed's character, 0 for none).</summary>
    public ChatResult Press(ChatKey key, char typed)
    {
        if (!Active) return ChatResult.None;
        switch (key)
        {
            case ChatKey.Escape: Active = false; _text.Clear(); return ChatResult.Cancelled;
            case ChatKey.Enter:
                Active = false;
                var line = _text.ToString().Trim();
                _text.Clear();
                if (line.Length == 0) return ChatResult.Cancelled;
                Sent = line;
                return ChatResult.Sent;
            case ChatKey.Backspace: if (_text.Length > 0) _text.Length--; break;
            default: if (typed >= ' ' && _text.Length < MaxLength) _text.Append(typed); break;
        }
        return ChatResult.None;
    }
}

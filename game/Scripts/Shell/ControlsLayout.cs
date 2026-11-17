using I76.Formats.Input;

namespace I76.Game.Shell;

/// <summary>
/// Control Configuration's rows on record 16's sheet: two columns of 23 ruled rows from y 47, a row the binding font's height + 5
/// apart. The original's commands fill the first 19 of each; the port's rows take the rest. Right: the multiplayer game keys
/// (Chat, Self Destruct, Respawn, Show All Names). Left: the hint (one row: left click lists, right click presses), the standings'
/// key (input.map's show_player_scores, held in a network game), the remaining game key (Reset Car Upright) and Mouse Options.
/// </summary>
public static class ControlsLayout
{
    /// <summary>The first row's top; a row's hit box is <see cref="HitH"/> tall from its top.</summary>
    public const int Top = 47, HitH = 16;
    /// <summary>The sheet's ruled rows in a column.</summary>
    public const int Rows = 23;
    /// <summary>The art's buttons (Calibrate, Restore, Done, Cancel) start at this y.</summary>
    public const int ButtonsY = 450;

    /// <summary>The original's commands: the entries of <c>Controls.Commands</c> on the left (no 0 and 3, the axes) and on the right.</summary>
    public static readonly int[] LeftRows = [1, 2, .. Enumerable.Range(4, 17)];
    public static readonly int[] RightRows = [.. Enumerable.Range(21, 19)];

    /// <summary>A row's entry for the standings (input.map's <see cref="BindingTable.Scores"/>, not one of the original's commands).</summary>
    public const int ScoresEntry = 90;
    /// <summary>A row's entry past the commands': the multiplayer game key <c>GameKeyMap.Rebindable[entry - GameEntry]</c>.</summary>
    public const int GameEntry = 100;
    /// <summary>How many of the game keys sit in the right column's empty rows (Chat, Self Destruct, Respawn, Show All Names); the rest are on the left's.</summary>
    public const int RightGameKeys = 4;

    /// <summary>The left column's rows past the commands: the hint, the standings, the other game keys, Mouse Options.</summary>
    public static int HintRow => LeftRows.Length;
    public static int ScoresRow => HintRow + 1;
    public static int LeftGameRow => ScoresRow + 1;
    public static int MouseRow => LeftGameRow + GameKeyMap.Rebindable.Length - RightGameKeys;

    public static int RowY(int row, int rowH) => Top + row * rowH;

    /// <summary>
    /// The entry of the row under the point, or -1: a command's index, <see cref="ScoresEntry"/> or <see cref="GameEntry"/> + n. The
    /// original's hit boxes are the binding's column alone (x 177..312, 475..612); the port takes the command's name too, the whole
    /// ruled row (x 30..312, 330..612). The hint and Mouse Options (a button of its own) are not rows to bind.
    /// </summary>
    public static int RowAt(int x, int y, int rowH)
    {
        if (y <= Top || (y - Top) % rowH >= HitH) return -1;
        int k = (y - Top) / rowH;
        bool left = 30 < x && x < 312, right = 330 < x && x < 612;
        if (right && k < RightRows.Length) return RightRows[k];
        if (right && k - RightRows.Length is >= 0 and var g && g < RightGameKeys) return GameEntry + g;
        if (!left) return -1;
        if (k < LeftRows.Length) return LeftRows[k];
        if (k == ScoresRow) return ScoresEntry;
        if (k >= LeftGameRow && k < MouseRow) return GameEntry + RightGameKeys + k - LeftGameRow;
        return -1;
    }
}

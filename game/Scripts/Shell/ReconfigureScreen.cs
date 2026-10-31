using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// I26's Reconfigure (not in the original; docs/deviations.md): while dead in a network melee, the car to come back in. The
/// join form's car panel over the network game, in a hosted shell: the form's art (record 18), its MAKE / MODEL and VARIANT
/// pickers and CONFIGURE CHASSIS (the player's own saved variants with them), and its JOIN GAME button to send the car to
/// the server, which checks it as a joining car and answers. Refused, the reason shows and the old car stays; taken, the
/// screen closes and the next respawn builds it. Esc or the form's other button leaves it unchanged.
/// </summary>
public sealed class ReconfigureScreen : ShellScreen
{
    /// <summary>The model and variant on show; a model of −1 is found from <see cref="CarFile"/> (the car the player has).</summary>
    public required int Model { get; set; }
    public required int Variant { get; set; }
    public string? CarFile { get; init; }
    /// <summary>The model to show when <see cref="CarFile"/> is none of the lists' (the launch's model index), or −1.</summary>
    public int FallbackModel { get; init; } = -1;
    /// <summary>Sends the car (its file, and its .vcf when this install's own variant: as a join's configuration).</summary>
    public required Action<string, byte[]?> Send { get; init; }
    public override bool EscapeOpensOptions => false;

    // the join form's hotspots (MeleeScreen's table): MAKE / MODEL, VARIANT, CONFIGURE CHASSIS, JOIN GAME, the way out
    private static readonly Dictionary<int, (int X0, int X1, int Y0, int Y1)> Hot = new()
    {
        [3] = (610, 621, 400, 415), [4] = (437, 449, 419, 434), [5] = (456, 616, 422, 435), [6] = (229, 369, 453, 471), [15] = (489, 630, 453, 471),
    };
    private ShellFont _font = null!;
    private SpriteBank _cars = null!;
    private MeleeData _data = null!;
    private string _status = "";
    private bool _waiting;
    private bool Nitro => Shell.Install.IsNitro;

    public override void Enter()
    {
        _font = Shell.Db.Font(0x45);
        _cars = Nitro ? Shell.Db.BankNative(54) : Shell.Db.Bank(0x3c);
        _data = Shell.Melee;
        if (Model < 0)
        {
            if (CarFile is { } car && _data.Find(car) is var (fm, fv)) (Model, Variant) = (fm, fv);
            else (Model, Variant) = (FallbackModel, 1);
        }
        if (_data.Models.Count == 0) { Shell.Options(); return; }   // no cars at all: nothing to pick from
        Model = Math.Clamp(Model, 0, _data.Models.Count - 1);
        Variant = Math.Clamp(Variant, 1, Math.Max(1, _data.Models[Model].Variants.Count));
        Repaint();
    }

    public override void Repaint()
    {
        Frame.LoadBackground(Shell.Db, 18);
        Draw();
    }

    private void Draw()
    {
        Frame.RestoreAll();
        Frame.Text(_font, 12, 45, "Reconfigure");
        // the join form's list box and rows; Nitro's art has them lower, five rows (MeleeScreen.DrawGames)
        Frame.Text(_font, 34, Nitro ? 130 : 109, "Come back in:");
        // the list's rows: what JOIN GAME and CANCEL do, then the server's answer
        var lines = new List<string> { "The car below.", "Join Game asks", "the server.", "Cancel keeps", "your car." };
        lines.AddRange(NoticePopup.Wrap(_font, _waiting ? "Asking the server..." : _status, 150, 1));
        int rows = Nitro ? 5 : 6, y0 = Nitro ? 171 : 149;
        // with the answer showing, the explanation gives way to it
        int first = Math.Max(0, lines.Count - rows);
        for (int k = 0; k < rows && first + k < lines.Count; k++) Frame.Text(_font, 34, y0 + 17 * k, AddressBookPopup.Fit(_font, lines[first + k], 170));
        MeleeScreen.DrawCar(Frame, _font, _cars, _data, Model, Variant, Shell.Parts);
    }

    public override IReadOnlyList<PadRegion> PadRegions => [.. Hot.Values.Select(r => PadRegion.Of(r))];

    private int HitId(Vector2I p) => Hot.Where(kv => kv.Value.X0 < p.X && p.X < kv.Value.X1 && kv.Value.Y0 < p.Y && p.Y < kv.Value.Y1).Select(kv => kv.Key).DefaultIfEmpty(-1).First();

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Shell.Options(); return; }
        if (!Shell.Clicked) return;
        int id = HitId(Shell.Mouse);
        if (id < 0) return;
        Shell.Sound(82);
        switch (id)
        {
            case 3:
                Shell.Modal(new ListPopup
                {
                    Sprite = 7, NativeSprite = Nitro ? 6 : null, X = 206, Y = 122, TextX = 22, TextY = 48, Visible = 9,
                    Items = _data.Models.Select(m => m.MakeModel).ToList(), Font = _font,
                    Up = new(18, 36, 12, 25), Down = new(18, 36, 204, 217), List = new(16, 214, 47, 198), Cancel = new(146, 215, 209, 225),
                    Picked = i => { if (i >= 0 && i != Model) (Model, Variant) = (i, 1); Repaint(); },
                });
                return;
            case 4:
                VariantPopups.Show(Shell, _font, _data, Model, Variant, Nitro, v => { Variant = v; Repaint(); },
                    (n, left) => { if (Variant >= n) Variant = Math.Max(1, Math.Min(Variant - (Variant > n ? 1 : 0), left)); }, Repaint);
                return;
            case 5:
                // CONFIGURE CHASSIS: the chassis form in place of the panel, then back here with the variant it saved
                var (m, v) = (Model, Variant);
                Shell.Overlay(null);
                Shell.Go(ChassisForm.Create(Shell, new MeleeChassis(m, v, n => { Variant = n; Back(); }, Back) { DoneAs = (nm, n) => { (Model, Variant) = (nm, n); Back(); } }));
                return;
            case 6:
                if (_waiting) break;
                string file = _data.Vcf(Model, Variant);
                byte[]? config = I76.Formats.Vfs.AssetVfs.Addon.TryGetValue(file, out var own) ? own
                    : Shell.Install.Resolve("addon", file) is { } path ? System.IO.File.ReadAllBytes(path) : null;
                _waiting = true;
                Send(file, config);
                break;
            case 15: Shell.Options(); return;
        }
        Draw();
    }

    private void Back()
    {
        Shell.Go(new HostScreen());
        Shell.Overlay(this);
    }

    /// <summary>The server's answer: refused, the reason over the form (the old car stays); taken, the caller closes the screen.</summary>
    public void Answer(I76.Net.ReconfigureAnswer a)
    {
        _waiting = false;
        if (a.Taken) return;
        _status = $"Refused: {a.Text}";
        Draw();
        Shell.Modal(new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = $"The server refused that car: {a.Text}", Closed = Draw });
    }
}

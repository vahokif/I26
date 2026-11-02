using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Vfs;
using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Weapons;

/// <summary>
/// A mounted weapon instance: the gizmo's numbers, the muzzle in vehicle space (HLOC ∘ GPOF), the
/// fire-control timers and the weapon's own hit points.
/// </summary>
public sealed class Weapon
{
    public required GizmoDef Def { get; init; }
    public required Hardpoint Hardpoint { get; init; }
    public uint Style { get; init; }
    /// <summary>1 front, 2 rear, 4 turret, 8/0x10 dropper.</summary>
    public int MountKind { get; init; }
    /// <summary>
    /// The muzzle in vehicle space (HLOC ∘ GPOF). A turret's muzzle object hangs under its gun node (GGEO),
    /// so it swings with the aim about <see cref="TurretPivot"/>: the barrel's tip, not a point fixed on the car.
    /// </summary>
    public Vector3 MuzzlePos
    {
        get => Style == 3 && TurretPivot is { } p ? p + MuzzleRot.ToWorld(BaseRot.ToLocal(RestMuzzle - p)) : RestMuzzle;
        init => RestMuzzle = value;
    }
    /// <summary>The muzzle with the weapon's aim at rest (a turret's at yaw and pitch 0).</summary>
    public Vector3 RestMuzzle { get; private init; }
    public Rows BaseRot { get; init; }
    /// <summary>
    /// A turret's gun node (the gizmo's T-style GN, in vehicle space): the original slips a node under the base (TU) at
    /// the gun's x and z, so the gun yaws about the vertical through its own origin and pitches about that origin, while
    /// the base stays put aims from here. Null for a fixed gun (or a turret gizmo without the nodes).
    /// </summary>
    public Vector3? TurretPivot { get; init; }
    public float AimYaw, AimPitch;
    /// <summary>Elevation of a fixed gun's muzzle (GunPitchWant the wanted one), up positive.</summary>
    public float GunPitch, GunPitchWant;
    public Rows MuzzleRot => Style == 3 ? TurretRot(AimYaw, AimPitch)
        : GunPitch != 0 ? Rows.Concat(Rows.AboutX(-GunPitch), BaseRot) : BaseRot;
    /// <summary>A turret's gun turned by this yaw and pitch (the view draws it between two ticks' aims).</summary>
    public Rows TurretRot(float yaw, float pitch) => Rows.Concat(Rows.AboutX(-pitch), Rows.Concat(Rows.AboutY(yaw), BaseRot));

    public int Hp;
    public int MaxHp;
    public int Damage;
    public int Ammo;
    public float WaitA;
    public float WaitB;
    public int BurstLeft;
    public bool Trigger;
    public bool WasFiring;
    public float LockTime;
    /// <summary>The turret is on its target.</summary>
    public bool OnTarget;

    public float HpRatio => MaxHp <= 0 ? 0 : (float)Hp / MaxHp;
}

/// <summary>
/// A vehicle's hardpoint record: up to five weapons
/// (ids 0–4) sorted as the original sorts them, the selected one and the
/// linked group the fire key works.
/// </summary>
public sealed class Armory
{
    public List<Weapon> Weapons { get; } = [];
    /// <summary>The weapons in WEPN order: the gizmo slots (the damage slots 10–16).</summary>
    public List<Weapon> LoadOrder { get; } = [];
    /// <summary>rec[1]: the selected hardpoint, −1 for none.</summary>
    public int Selected = -1;
    /// <summary>rec[2..6]: hardpoints linked with the selected one.</summary>
    public List<int> Group { get; } = [];
    public bool Link;
    /// <summary>
    /// A respawned car keeps the old one's link. The original's link flag is a global only the mission's
    /// start clears, so a new car's record found it still on and built the group at once; the port also keeps the
    /// selection and the group when the new car mounts the same weapons hardpoint for hardpoint, where the original's
    /// new record started from no selection.
    /// </summary>
    public void InheritLinks(Armory? old)
    {
        if (old is null || ReferenceEquals(old, this)) return;
        Link = old.Link;
        if (!Link || old.Weapons.Count != Weapons.Count) return;
        for (int i = 0; i < Weapons.Count; i++)
            if (old.Weapons[i].Hardpoint.Id != Weapons[i].Hardpoint.Id || old.Weapons[i].Def.Name != Weapons[i].Def.Name) return;
        if (old.Selected < 0 || old.Selected >= Weapons.Count) return;
        Selected = old.Selected;
        Group.Clear();
        foreach (var g in old.Group) if (g >= 0 && g < Weapons.Count) Group.Add(g);
    }
    public float WeaponMass => Weapons.Sum(w => w.Def.Mass);
    /// <summary>
    /// The driver's two handguns, outside the five hardpoints:
    /// [0] the right arm (crew channel 1, record slot 6), [1] the left (channel
    /// 2, slot 5). Their hardpoint is type 6, facing 3 (right) or 4 (left), so
    /// Gives them mount kind 8 or 0x10: the side arcs, which tip
    /// their muzzle at the car abeam. They fire only
    /// while that arm is raised to its window.
    /// </summary>
    public Weapon?[] Handguns { get; } = new Weapon?[2];

    /// <summary>
    /// the definition, built in code rather than read from a .gdf:
    /// class 1, tier = the channel, 1 damage, 45 HP, 2.5 s rounds at 120 with
    /// no drop, 3 shots/s in bursts of one, unlimited ammo (0x0fffffff), 0.2°
    /// spread, kind 0x12, no muzzle flash, h45ch.wav. Its ordnance
    /// carries damage mask 4, tracer 0xf0 and the impacts xbulg1/wmgr2 (terrain),
    /// xbulc1/wmgr1 (vehicles), xbulb1/hssr1 and hssr2 (the fourth effect slot is
    /// never written: the code loads xbulb1 into the third twice).
    /// </summary>
    public static GizmoDef HandgunDef(int channel) => new(
        "handgn", ".45 Handgun", 1, (uint)channel, 1, 45, 0, 2.5f, 0, 3f, 1, 120f, 0x12, 0x0fffffff, 0.2f, "", "h45ch.wav", 0, [], [],
        new OrdnanceDef(0x12, 120f, 0f, 4, 0xf0, ["xbulg1.xdf", "xbulc1.xdf", "xbulb1.xdf", ""], ["wmgr2.wav", "wmgr1.wav", "hssr1.wav", "hssr2.wav"]), [])
    {
        PlateOn = "3sp_empty_on", PlateOff = "3sp_empty_off",
    };

    /// <summary>
    /// Arms the driver's handguns for the arms the cockpit has. The muzzle is
    /// the part of type 0x2b (right) or 0x2c (left), GRPF/GLPF, whose own
    /// transform is taken as the car-frame placement (the engine hides the
    /// part); without one, the code's fixed points: (0.41, 1.14, 0.05) turned
    /// 88° right, or (−0.81, 1.16, −0.12) turned 111° left, tipped 5°.
    /// </summary>
    public void AddHandguns(Bwd2File vdf, bool right, bool left)
    {
        var nodes = vdf.SceneNodes().Where(t => t.Tag == "VGEO").SelectMany(t => t.Nodes).ToList();
        for (int ch = 1; ch <= 2; ch++)
        {
            if (ch == 1 ? !right : !left) continue;
            string code = ch == 1 ? "GRPF" : "GLPF";
            var node = nodes.FirstOrDefault(n => n.Name.Length >= 8 && n.Name[2] == '5' && n.Name[4..8].Equals(code, StringComparison.OrdinalIgnoreCase))
                ?? nodes.FirstOrDefault(n => n.Name.Length >= 8 && n.Name[4..8].Equals(code, StringComparison.OrdinalIgnoreCase));
            Vector3 pos; Rows rot;
            if (node is not null)
            {
                var xf = Affine.From(node.Transform);
                pos = xf.T; rot = RowsOf(xf);
            }
            else
            {
                pos = ch == 1 ? new Vector3(0.41f, 1.14f, 0.05f) : new Vector3(-0.81f, 1.16f, -0.12f);
                rot = Rows.Concat(Rows.AboutX(0.0872665f), Rows.AboutY(ch == 1 ? 1.5359f : -1.93731f));
            }
            var def = HandgunDef(ch);
            var hp = new Hardpoint("handgn", ch == 1 ? 6u : 5u, 0, 0, node?.Transform ?? new Transform([1, 0, 0, 0, 1, 0, 0, 0, 1], [pos.X, pos.Y, pos.Z]));
            Handguns[ch - 1] = new Weapon
            {
                Def = def, Hardpoint = hp, MountKind = ch == 1 ? 8 : 0x10, MuzzlePos = pos, BaseRot = rot,
                Hp = (int)def.HitPoints, MaxHp = (int)def.HitPoints, Damage = (int)def.Damage, Ammo = (int)def.Ammo,
            };
        }
    }

    /// <summary>Key: front S/I 1, front P/turret 2, back S/I 3, back D 4, else 9.</summary>
    public static int SortKey(uint style, uint facing) => (facing, style) switch
    {
        (1, 2 or 5) => 1,
        (1, 1 or 3) => 2,
        (2, 2 or 5) => 3,
        (2, 4) => 4,
        _ => 9,
    };

    /// <summary>
    /// For a side (S, style 2) gizmo: on the left side of the car (a front hardpoint at x &lt; 0, a back one at
    /// x &gt; 0) the muzzle's own matrix is turned about before it is mounted, entries 0, 1, 2, 3, 6 and 7 of the nine
    /// negated (the rows being the axes, the barrel's direction loses its x and y signs). The hardpoint there is rolled the
    /// other way round, so without this a cluster bomb or mortar, whose barrel leans 16 degrees, leans down into the car
    /// instead of up. The muzzle in vehicle space (HLOC ∘ GPOF).
    /// </summary>
    public static Affine MuzzleOn(GizmoDef g, Hardpoint hp, uint style)
    {
        var at = Affine.From(hp.Transform);
        if (g.GunPoint(style) is not { } gp) return at;
        return at.Compose(Affine.From(MirroredGunPoint(gp, hp, style)));
    }

    private static Transform MirroredGunPoint(Transform gp, Hardpoint hp, uint style)
    {
        float x = hp.Transform.Translation[0];
        if (style != 2 || !((hp.Facing == 1 && x < 0) || (hp.Facing == 2 && x > 0))) return gp;
        var r = (float[])gp.Rotation.Clone();
        foreach (int k in new[] { 0, 1, 2, 3, 6, 7 }) r[k] = -r[k];
        return gp with { Rotation = r };
    }

    private static Rows RowsOf(Affine a) => new(new(a.R[0], a.R[3], a.R[6]), new(a.R[1], a.R[4], a.R[7]), new(a.R[2], a.R[5], a.R[8]));

    /// <summary>WEPN slots onto HLOC hardpoints; a slot without its hardpoint loads nothing (the handler's "wrldr").</summary>
    public static Armory Create(AssetVfs vfs, Bwd2File vcf, Bwd2File vdf)
    {
        var a = new Armory();
        var hps = Hardpoint.Read(vdf);
        foreach (var m in vcf.WeaponMounts().Where(w => !w.Empty))
        {
            if (hps.FirstOrDefault(h => h.Id == m.Slot) is not { } hp) continue;
            if (vfs.TryRead(m.Gizmo) is not { } gd || GizmoDef.Read(m.Gizmo, gd) is not { } g) continue;
            uint style = g.StyleOn(hp);
            var at = Affine.From(hp.Transform);
            var muzzle = MuzzleOn(g, hp, style);
            var rot = RowsOf(muzzle);
            Vector3? pivot = style == 3 && g.TurretGun() is { } gn ? at.Apply(gn) : null;
            int kind = style switch { 3 => 4, 6 => hp.Facing == 3 ? 8 : 0x10, _ => rot.Forward.Z < 0 ? 2 : 1 };
            a.Weapons.Add(new Weapon
            {
                Def = g, Hardpoint = hp, Style = style, MountKind = kind, MuzzlePos = muzzle.T, BaseRot = rot, TurretPivot = pivot,
                Hp = (int)g.HitPoints, MaxHp = (int)g.HitPoints, Damage = (int)g.Damage, Ammo = (int)g.Ammo,
            });
        }
        a.LoadOrder.AddRange(a.Weapons);
        // ids 0–4 sorted by key, ties by id; ids > 4 would go to fixed entries 5 and 6 (none ship)
        var sorted = a.Weapons.Where(w => w.Hardpoint.Id <= 4).OrderBy(w => SortKey(w.Style, w.Hardpoint.Facing)).ThenBy(w => w.Hardpoint.Id).ToList();
        a.Weapons.Clear();
        a.Weapons.AddRange(sorted);
        if (a.Weapons.Count > 0) a.Selected = 0;
        return a;
    }

    /// <summary>
    /// The player's selection step in, once a frame after the triggers: only while some hardpoint has HP
    /// left, a selected weapon shot to nothing (its HP ratio 0) cycles as the cycle key does, the link key flips <see cref="Link"/>, and then, every frame, a link with an empty group builds
    /// one or no link clears it. With every hardpoint dead the keys do nothing.
    /// Returns how many times the original asks for cammo.wav this frame: a selection that changed, each weapon linked
    /// or unlinked.
    /// </summary>
    public int Select(bool cycleKey, bool linkKey, out bool selectionChanged, bool cyclePrevKey = false)
    {
        selectionChanged = false;
        if (!Weapons.Take(5).Any(w => w.HpRatio > 0)) return 0;
        int sounds = 0;
        if (Selected >= 0 && Selected < Weapons.Count && Weapons[Selected].HpRatio == 0) cycleKey = true;
        if (cycleKey && Cycle()) { selectionChanged = true; sounds++; }
        else if (cyclePrevKey && CyclePrev()) { selectionChanged = true; sounds++; }
        if (linkKey) Link = !Link;
        sounds += Link ? LinkGroup() : Unlink();
        return sounds;
    }

    /// <summary>
    /// From the selected hardpoint onward (wrapping), the first not already in the group with HP left. If
    /// it finds none the selection and the group stay as they are; otherwise the group is cleared (a link builds it
    /// again in the same frame). True when the selection changed.
    /// </summary>
    public bool Cycle()
    {
        if (Selected < 0 || Selected >= Weapons.Count) return false;
        for (int k = 1; k < Weapons.Count; k++)
        {
            int i = (Selected + k) % Weapons.Count;
            if (Group.Contains(i) || Weapons[i].HpRatio <= 0) continue;
            Selected = i;
            Group.Clear();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Steps the selected hardpoint backward (upward on the HUD weapon rack), skipping hardpoints in the current
    /// linked group or without HP left. True when the selection changed.
    /// </summary>
    public bool CyclePrev()
    {
        if (Selected < 0 || Selected >= Weapons.Count) return false;
        for (int k = 1; k < Weapons.Count; k++)
        {
            int i = (Selected + Weapons.Count - k) % Weapons.Count;
            if (Group.Contains(i) || Weapons[i].HpRatio <= 0) continue;
            if (Link)
            {
                var w = Weapons[i];
                if (!((w.Def.Class is 3 or 4) && w.Def.Tier == 4))
                    for (int m = 0; m < Weapons.Count; m++)
                    {
                        var first = Weapons[m];
                        if (first.MountKind == w.MountKind && first.Def.Class == w.Def.Class && first.HpRatio > 0
                            && !((first.Def.Class is 3 or 4) && first.Def.Tier == 4))
                        {
                            i = m;
                            break;
                        }
                    }
            }
            Selected = i;
            Group.Clear();
            return true;
        }
        return false;
    }

    /// <summary>
    /// While the link is on: only with the group empty, every other hardpoint after the selected one
    /// (wrapping) with the selected one's class and mount kind and HP left, bar class 3 or 4 tier 4 (the selected one
    /// itself may be one). Returns how many it linked.
    /// </summary>
    private int LinkGroup()
    {
        if (Selected < 0 || Selected >= Weapons.Count || Group.Count > 0) return 0;
        var s = Weapons[Selected];
        for (int k = 1; k < Weapons.Count; k++)
        {
            int i = (Selected + k) % Weapons.Count;
            var w = Weapons[i];
            if (w.MountKind != s.MountKind || w.Def.Class != s.Def.Class || w.HpRatio <= 0) continue;
            if ((w.Def.Class is 3 or 4) && w.Def.Tier == 4) continue;
            Group.Add(i);
        }
        return Group.Count;
    }

    /// <summary>While the link is off: the group is cleared. Returns how many it unlinked.</summary>
    private int Unlink()
    {
        if (Selected < 0 || Selected >= Weapons.Count) return 0;
        int n = Group.Count;
        Group.Clear();
        return n;
    }

    /// <summary>
    /// The player: the fire key works the selected weapon and its
    /// group, the number keys their hardpoints (in sorted order). A broken
    /// weapon only clicks. Returns the indices that tried and found the weapon broken.
    /// </summary>
    public List<int> PlayerTriggers(bool fire, IReadOnlyList<bool> keys)
    {
        var broken = new List<int>();
        for (int i = 0; i < Weapons.Count && i < 5; i++)
        {
            var w = Weapons[i];
            bool inGroup = i == Selected || Group.Contains(i);
            if ((inGroup && fire) || (i < keys.Count && keys[i]))
            {
                if (w.Hp <= 0) { w.Trigger = false; broken.Add(i); }
                // class 4 tier 4 (the 4Get-U-Not) needs a lock of 1.6 s: the original reads it after this frame's aims
                // have built it, so Combat's lock pass takes the request back while the lock is short
                else w.Trigger = true;
            }
            else w.Trigger = false;
        }
        return broken;
    }
}

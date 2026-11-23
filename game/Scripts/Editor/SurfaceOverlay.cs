using Godot;
using I76.Editor;

namespace I76.Game.Editor;

/// <summary>
/// The surface map drawn over the terrain while the Surface tool is on: a window of <see cref="Size"/> x <see cref="Size"/>
/// samples round the camera, one texel per sample coloured by its surface type (<see cref="SurfaceTypes"/>), read by the
/// terrain shaders (brush_cursor.gdshaderinc, both looks) through the i76_surface globals. The window follows the camera and
/// repaints the samples an edit reached.
/// </summary>
public sealed class SurfaceOverlay
{
    public const int Size = 768;
    // strong enough to read over any ground; an edge between two types is drawn dark and opaque so a stroke's outline always shows
    private const float Alpha = 0.78f;
    private readonly IMapDocument _doc;
    private readonly byte[] _pixels = new byte[Size * Size * 4];
    private ImageTexture? _texture;
    private int _x0 = int.MinValue, _z0 = int.MinValue;
    private SampleRect? _dirty;
    private bool _shown;

    public SurfaceOverlay(IMapDocument doc) { _doc = doc; }

    /// <summary>Samples an edit reached (they repaint at the next <see cref="Update"/>).</summary>
    public void Touch(SampleRect r) => _dirty = _dirty is { } d ? d.Union(r) : r;

    /// <summary>Shows the overlay round a point (sample coordinates), or hides it.</summary>
    public void Update(bool on, double sampleX, double sampleZ)
    {
        if (!on)
        {
            if (_shown) RenderingServer.GlobalShaderParameterSet("i76_surface_on", Vector4.Zero);
            _shown = false;
            return;
        }
        int cx = (int)sampleX, cz = (int)sampleZ;
        bool moved = _x0 == int.MinValue || Math.Abs(cx - (_x0 + Size / 2)) > Size / 6 || Math.Abs(cz - (_z0 + Size / 2)) > Size / 6;
        if (moved || !_shown)
        {
            _x0 = cx - Size / 2; _z0 = cz - Size / 2;
            Fill(_x0, _z0, _x0 + Size - 1, _z0 + Size - 1);
            _dirty = null;
            Upload(true);
        }
        else if (_dirty is { } d)
        {
            _dirty = null;
            // one sample more round the edit: its neighbours' edges change with it
            Fill(Math.Max(d.X0 - 1, _x0), Math.Max(d.Z0 - 1, _z0), Math.Min(d.X1 + 1, _x0 + Size - 1), Math.Min(d.Z1 + 1, _z0 + Size - 1));
            Upload(false);
        }
        _shown = true;
        RenderingServer.GlobalShaderParameterSet("i76_surface_on", new Vector4(1, 0, 0, 0));
    }

    private void Fill(int x0, int z0, int x1, int z1)
    {
        // the ids of the rectangle and a sample round it (the edges look at the neighbours), read once
        int w = x1 - x0 + 3, h = z1 - z0 + 3;
        var ids = new sbyte[w * h];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
                ids[z * w + x] = (sbyte)(_doc.Locate(x0 - 1 + x, z0 - 1 + z, out int tile, out int off) ? SurfaceTypes.Of(_doc.GetSample(tile, off)) : -1);
        for (int sz = z0; sz <= z1; sz++)
            for (int sx = x0; sx <= x1; sx++)
            {
                int i = ((sz - _z0) * Size + (sx - _x0)) * 4;
                int at = (sz - z0 + 1) * w + (sx - x0 + 1);
                int id = ids[at];
                if (id < 0) { _pixels[i + 3] = 0; continue; }
                var info = SurfaceTypes.All[id];
                bool Differs(int o) => ids[o] >= 0 && ids[o] != id;
                bool edge = Differs(at - 1) || Differs(at + 1) || Differs(at - w) || Differs(at + w);
                float k = edge ? 0.3f : 1f;
                _pixels[i] = (byte)(info.R * k); _pixels[i + 1] = (byte)(info.G * k); _pixels[i + 2] = (byte)(info.B * k);
                _pixels[i + 3] = (byte)((edge ? 0.95f : Alpha) * 255);
            }
    }

    private void Upload(bool rect)
    {
        var img = Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, _pixels);
        if (_texture is null)
        {
            _texture = ImageTexture.CreateFromImage(img);
            RenderingServer.GlobalShaderParameterSet("i76_surface", _texture);
        }
        else _texture.Update(img);
        if (rect)
        {
            // texel i covers the sample i (centred on it): x = 5 * (sample - 0.5)
            float k = (float)Units.SampleSize;
            RenderingServer.GlobalShaderParameterSet("i76_surface_rect", new Vector4((_x0 - 0.5f) * k, (_z0 - 0.5f) * k, 1f / (Size * k), 1f / (Size * k)));
        }
    }
}

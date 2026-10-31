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
    private const float Alpha = 0.62f;
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
            Fill(Math.Max(d.X0, _x0), Math.Max(d.Z0, _z0), Math.Min(d.X1, _x0 + Size - 1), Math.Min(d.Z1, _z0 + Size - 1));
            Upload(false);
        }
        _shown = true;
        RenderingServer.GlobalShaderParameterSet("i76_surface_on", new Vector4(1, 0, 0, 0));
    }

    private void Fill(int x0, int z0, int x1, int z1)
    {
        for (int sz = z0; sz <= z1; sz++)
            for (int sx = x0; sx <= x1; sx++)
            {
                int i = ((sz - _z0) * Size + (sx - _x0)) * 4;
                if (!_doc.Locate(sx, sz, out int tile, out int off)) { _pixels[i + 3] = 0; continue; }
                var info = SurfaceTypes.All[SurfaceTypes.Of(_doc.GetSample(tile, off))];
                _pixels[i] = info.R; _pixels[i + 1] = info.G; _pixels[i + 2] = info.B; _pixels[i + 3] = (byte)(Alpha * 255);
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

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Launcher.App;

/// <summary>
/// Small textures the launcher draws itself (no picture files): stone, parchment, mist, ember and wood. Each is a
/// 256 by 256 tile (512 for mist and ember) that repeats without seams, made from smooth noise. A texture is either an overlay (light and dark
/// specks over whatever is behind it) or filled with a base colour (for panels).
/// </summary>
public static class DrawnTextures
{
    static int Size = 256;
    static readonly Dictionary<string, WriteableBitmap> Cache = [];

    /// <summary>A tiled brush of a drawn texture: over the window when <paramref name="baseColour"/> is null, else
    /// filled with that colour (its alpha kept). Null for "none" or an unknown name.</summary>
    public static IBrush? Brush(string name, double strength, Color? baseColour = null)
    {
        WriteableBitmap? bitmap = Bitmap(name.ToLowerInvariant(), strength, baseColour);
        return bitmap is null ? null : Tiled(bitmap);
    }

    public static ImageBrush Tiled(Bitmap bitmap) => new(bitmap)
    {
        TileMode = TileMode.Tile, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
        DestinationRect = new RelativeRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height, RelativeUnit.Absolute),
    };

    static WriteableBitmap? Bitmap(string name, double strength, Color? baseColour)
    {
        if (name is not ("stone" or "parchment" or "mist" or "ember" or "wood")) return null;
        string key = $"{name}|{strength:0.00}|{baseColour}";
        if (Cache.TryGetValue(key, out WriteableBitmap? cached)) return cached;
        Size = name is "mist" or "ember" ? 512 : 256;   // big soft shapes need a bigger tile, or the repeat shows
        float[] shade = Shade(name);   // -1 (dark) .. +1 (light) per pixel
        WriteableBitmap bitmap = new(new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (ILockedFramebuffer fb = bitmap.Lock())
        {
            byte[] row = new byte[Size * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float v = Math.Clamp(shade[y * Size + x] * (float)strength, -1f, 1f);
                    byte b, g, r, a;
                    if (baseColour is { } c)
                    {
                        // the base colour, lighter or darker
                        float f = 1 + v * 0.55f;
                        a = c.A;
                        r = Premul(Light(c.R, f), a); g = Premul(Light(c.G, f), a); b = Premul(Light(c.B, f), a);
                    }
                    else
                    {
                        // black or light specks over what is behind (orange sparks for ember)
                        a = (byte)Math.Clamp(MathF.Abs(v) * 255f, 0, 255);
                        (byte lr, byte lg, byte lb) = name == "ember" ? ((byte)255, (byte)150, (byte)50) : ((byte)255, (byte)244, (byte)220);
                        (r, g, b) = v >= 0 ? (Premul(lr, a), Premul(lg, a), Premul(lb, a)) : ((byte)0, (byte)0, (byte)0);
                    }
                    int i = x * 4;
                    row[i] = b; row[i + 1] = g; row[i + 2] = r; row[i + 3] = a;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
            }
        }
        Cache[key] = bitmap;
        return bitmap;
    }

    static byte Light(byte v, float f) => (byte)Math.Clamp(f <= 1 ? v * f : v + (255 - v) * (f - 1), 0, 255);
    static byte Premul(byte v, byte a) => (byte)(v * a / 255);

    /// <summary>How light or dark each pixel of a texture is, -1 to 1.</summary>
    static float[] Shade(string name)
    {
        float[] s = new float[Size * Size];
        Random rnd = new(name.Length * 7919 + name[0]);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float v = name switch
                {
                    // rough rock: mid noise, fine grit and the mortar lines of big blocks
                    "stone" => Stone(x, y),
                    // old paper: soft blotches, darker edges of each blotch and a few fibres
                    "parchment" => 0.55f * Fbm(x, y, 16, 4, 3) + 0.25f * Fbm(x, y, 64, 2, 5) - 0.15f * MathF.Abs(Fbm(x, y, 32, 3, 9)),
                    // drifting fog: big, very soft clouds
                    "mist" => 0.8f * Fbm(x, y, 4, 4, 11),
                    // glowing sparks over soot
                    "ember" => -0.35f + 0.35f * Fbm(x, y, 8, 3, 13),
                    // planks: long grain lines across, a little knot noise
                    "wood" => Wood(x, y),
                    _ => 0,
                };
                s[y * Size + x] = v;
            }
        if (name == "ember")
            for (int n = 0; n < 90; n++)
            {
                int cx = rnd.Next(Size), cy = rnd.Next(Size), size = rnd.Next(4) == 0 ? 3 : 2;
                float glow = 0.45f + 0.55f * (float)rnd.NextDouble();
                for (int dy = -size; dy <= size; dy++)
                    for (int dx = -size; dx <= size; dx++)
                    {
                        float d = MathF.Sqrt(dx * dx + dy * dy);
                        if (d > size) continue;
                        int i = ((cy + dy + Size) % Size) * Size + (cx + dx + Size) % Size;
                        s[i] = MathF.Max(s[i], glow * (1 - d / (size + 0.2f)));
                    }
            }
        return s;
    }

    static float Stone(int x, int y)
    {
        float v = 0.55f * Fbm(x, y, 8, 4, 1) + 0.3f * (Hash(x, y, 77) * 2 - 1) * 0.5f;
        // blocks 128 wide and 64 high, every other row shifted by half a block
        int row = y / 64, bx = (x + (row % 2) * 64) % 128, by = y % 64;
        int edge = Math.Min(Math.Min(bx, 128 - bx), Math.Min(by, 64 - by));
        if (edge < 2) v -= 0.75f;
        else if (edge < 4) v += 0.25f;   // a lit edge next to the line
        return v;
    }

    static float Wood(int x, int y)
    {
        float warp = 6f * Fbm(x, y, 4, 2, 21);
        float grain = MathF.Sin((y + warp) * MathF.PI * 2 / 8f) * 0.25f + MathF.Sin((y + warp * 2) * MathF.PI * 2 / 32f) * 0.2f;
        float plank = (y % 64) < 2 ? -0.7f : 0;
        return grain + 0.3f * Fbm(x, y, 32, 2, 23) + plank;
    }

    /// <summary>Smooth noise that repeats every 256 pixels: <paramref name="cells"/> cells across, several octaves.</summary>
    static float Fbm(int x, int y, int cells, int octaves, int seed)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * (Value(x, y, cells << o, seed + o) * 2 - 1);
            norm += amp;
            amp *= 0.5f;
        }
        return sum / norm;
    }

    static float Value(int x, int y, int cells, int seed)
    {
        float fx = x * cells / (float)Size, fy = y * cells / (float)Size;
        int x0 = (int)fx, y0 = (int)fy;
        float tx = fx - x0, ty = fy - y0;
        tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
        int x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells;
        x0 %= cells; y0 %= cells;
        float a = Hash(x0, y0, seed), b = Hash(x1, y0, seed), c = Hash(x0, y1, seed), d = Hash(x1, y1, seed);
        return a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
    }

    static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}

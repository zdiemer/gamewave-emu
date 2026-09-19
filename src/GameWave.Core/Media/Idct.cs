using System.Runtime.CompilerServices;

namespace GameWave.Media;

/// <summary>
/// The 8x8 inverse DCT, computed separably in floating point to the accuracy IEEE 1180
/// asks for, with short cuts for the many blocks that hold only a DC term or a few rows.
/// </summary>
internal static class Idct
{
    // Cos[x * 8 + u] = C(u) / 2 * cos((2x + 1) u pi / 16)
    static readonly float[] Cos = Build();

    static float[] Build()
    {
        var t = new float[64];
        for (int x = 0; x < 8; x++)
        {
            for (int u = 0; u < 8; u++)
            {
                double c = u == 0 ? Math.Sqrt(0.5) : 1.0;
                t[x * 8 + u] = (float)(c / 2 * Math.Cos((2 * x + 1) * u * Math.PI / 16));
            }
        }
        return t;
    }

    [ThreadStatic] static float[]? _tmp;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Transform(int[] block)
    {
        // DC only: every output is the same.
        bool dcOnly = true;
        for (int i = 1; i < 64; i++)
        {
            if (block[i] != 0)
            {
                dcOnly = false;
                break;
            }
        }
        if (dcOnly)
        {
            int v = (int)MathF.Round(block[0] / 8f, MidpointRounding.AwayFromZero);
            v = v < -256 ? -256 : v > 255 ? 255 : v;
            for (int i = 0; i < 64; i++)
                block[i] = v;
            return;
        }

        var tmp = _tmp ??= new float[64];
        // Rows: tmp[y][x] = sum_u block[y][u] * Cos[x][u]
        for (int y = 0; y < 8; y++)
        {
            int r = y * 8;
            bool empty = true;
            for (int u = 0; u < 8; u++)
            {
                if (block[r + u] != 0)
                {
                    empty = false;
                    break;
                }
            }
            if (empty)
            {
                for (int x = 0; x < 8; x++)
                    tmp[r + x] = 0;
                continue;
            }
            float b0 = block[r], b1 = block[r + 1], b2 = block[r + 2], b3 = block[r + 3];
            float b4 = block[r + 4], b5 = block[r + 5], b6 = block[r + 6], b7 = block[r + 7];
            for (int x = 0; x < 8; x++)
            {
                int c = x * 8;
                tmp[r + x] = b0 * Cos[c] + b1 * Cos[c + 1] + b2 * Cos[c + 2] + b3 * Cos[c + 3]
                           + b4 * Cos[c + 4] + b5 * Cos[c + 5] + b6 * Cos[c + 6] + b7 * Cos[c + 7];
            }
        }
        // Columns.
        for (int x = 0; x < 8; x++)
        {
            float t0 = tmp[x], t1 = tmp[8 + x], t2 = tmp[16 + x], t3 = tmp[24 + x];
            float t4 = tmp[32 + x], t5 = tmp[40 + x], t6 = tmp[48 + x], t7 = tmp[56 + x];
            for (int y = 0; y < 8; y++)
            {
                int c = y * 8;
                float v = t0 * Cos[c] + t1 * Cos[c + 1] + t2 * Cos[c + 2] + t3 * Cos[c + 3]
                        + t4 * Cos[c + 4] + t5 * Cos[c + 5] + t6 * Cos[c + 6] + t7 * Cos[c + 7];
                int iv = (int)MathF.Round(v, MidpointRounding.AwayFromZero);
                block[y * 8 + x] = iv < -256 ? -256 : iv > 255 ? 255 : iv;
            }
        }
    }
}

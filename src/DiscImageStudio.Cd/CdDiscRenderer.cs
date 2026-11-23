using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>Draws a burned CD-R the way it looks under a lamp: AZO-blue dye, darker burned
/// marks, a clear hub and a soft rainbow reflection.</summary>
internal static class CdDiscRenderer
{
    private const double DiscRadiusMm = 60.0, HoleRadiusMm = 7.5, ClearRadiusMm = 22.5;

    internal static byte[] Render(string sourceImage, CdDiscParameters parameters, ToneProfile tone,
        long drawStartByte, long drawEndByte, int size, CancellationToken cancellationToken)
    {
        // The engine's own projection of the art, at its native preview scale.
        int warpSize = Math.Clamp(size, 256, 2048);
        byte[] warp = CdTrackGenerator.PreviewWarpPixels(sourceImage, parameters, parameters, warpSize,
            samplesPerSector: 24, tone, drawStartByte, drawEndByte, cancellationToken);
        double warpMmPerPixel = parameters.PreviewImageOuterRadiusMm / (warpSize / 2.0);
        double drawInner = parameters.RadiusFromByte(drawStartByte);
        double drawOuter = parameters.RadiusFromByte(Math.Min(drawEndByte, parameters.TotalBytes));
        double dataInner = parameters.InnerRadiusMm, dataOuter = parameters.OutputOuterRadiusMm;

        byte[] pixels = new byte[size * size * 4];
        double mm = 2 * DiscRadiusMm / size;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < size; x++)
            {
                double dx = (x + 0.5 - size / 2.0) * mm, dy = (y + 0.5 - size / 2.0) * mm;
                double r = Math.Sqrt(dx * dx + dy * dy), angle = Math.Atan2(dy, dx);
                (double R, double G, double B) c;
                if (r > DiscRadiusMm || r < HoleRadiusMm)
                    c = (27, 29, 39); // background / centre hole
                else if (r < ClearRadiusMm)
                    c = r is > 16 and < 17.2 ? (110, 112, 120) : (90, 92, 98); // clear hub + stacking ring
                else if (r > DiscRadiusMm - 0.6)
                    c = (125, 130, 145); // polished rim
                else
                {
                    double level; // 1 = unburned dye, 0 = fully burned
                    if (r < dataInner) level = 1.25; // lead-in mirror band
                    else if (r > dataOuter) level = 1.0;
                    else if (r < drawInner || r > drawOuter) level = 0.5 + 0.07 * Math.Sin(r * 9.0); // music/file data texture
                    else level = SampleWarp(warp, warpSize, warpMmPerPixel, dx, dy) / 255.0;
                    c = Dye(level);
                    c = Sheen(c, angle, r);
                }
                int o = (y * size + x) * 4;
                pixels[o] = ToByte(c.B); pixels[o + 1] = ToByte(c.G); pixels[o + 2] = ToByte(c.R); pixels[o + 3] = 255;
            }
        });
        return pixels;
    }

    private static double SampleWarp(byte[] warp, int size, double mmPerPixel, double dx, double dy)
    {
        // Bilinear, using only pixels the projection actually covered.
        double px = dx / mmPerPixel + size / 2.0 - 0.5, py = dy / mmPerPixel + size / 2.0 - 0.5;
        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        double fx = px - x0, fy = py - y0, sum = 0, weight = 0;
        for (int j = 0; j < 2; j++)
        for (int i = 0; i < 2; i++)
        {
            int x = x0 + i, y = y0 + j;
            if ((uint)x >= (uint)size || (uint)y >= (uint)size) continue;
            int o = (y * size + x) * 4;
            if (warp[o + 3] == 0) continue;
            double w = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
            sum += warp[o] * w; weight += w;
        }
        return weight > 0 ? sum / weight : 255;
    }

    private static (double, double, double) Dye(double level)
    {
        (double R, double G, double B) burned = (18, 20, 48), unburned = (62, 78, 170);
        return (burned.R + (unburned.R - burned.R) * level,
            burned.G + (unburned.G - burned.G) * level,
            burned.B + (unburned.B - burned.B) * level);
    }

    private static (double, double, double) Sheen((double R, double G, double B) c, double angle, double r)
    {
        double hue = angle * 2 + r * 0.08;
        double sheen = Math.Pow(Math.Cos(2 * (angle - 0.6)), 8) * 0.35;
        double R = c.R * (1 - sheen) + (Math.Sin(hue) * 0.5 + 0.5) * 255 * sheen;
        double G = c.G * (1 - sheen) + (Math.Sin(hue + 2.1) * 0.5 + 0.5) * 255 * sheen;
        double B = c.B * (1 - sheen) + (Math.Sin(hue + 4.2) * 0.5 + 0.5) * 255 * sheen;
        double spec = (Glint(angle - 0.6) + Glint(angle + 2.54)) * 0.25;
        return (R + (255 - R) * spec, G + (255 - G) * spec, B + (255 - B) * spec);
    }

    private static double Glint(double a)
    {
        double d = Math.IEEERemainder(a, 2 * Math.PI);
        return Math.Exp(-d * d / 0.02);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}

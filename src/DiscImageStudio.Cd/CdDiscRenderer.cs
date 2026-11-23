using DiscImageStudio.Core;

namespace DiscImageStudio.Cd;

/// <summary>Draws a burned CD-R to scale (see <see cref="CdPhysicalLayout"/>) the way it looks
/// under a lamp: AZO-blue dye, darker burned marks, clear hub and a soft rainbow reflection.
/// The image spans exactly the 120 mm disc, so pixels map linearly to millimetres.</summary>
internal static class CdDiscRenderer
{
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
        CdPhysicalLayout disc = CdPhysicalLayout.For(parameters);

        byte[] pixels = new byte[size * size * 4];
        double mm = 2 * disc.DiscRadiusMm / size;
        Parallel.For(0, size, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int x = 0; x < size; x++)
            {
                double dx = (x + 0.5 - size / 2.0) * mm, dy = (y + 0.5 - size / 2.0) * mm;
                double r = Math.Sqrt(dx * dx + dy * dy), angle = Math.Atan2(dy, dx);
                (double R, double G, double B) c;
                if (r > disc.DiscRadiusMm || r < disc.CenterHoleRadiusMm)
                    c = (27, 29, 39); // background / centre hole
                else if (r < disc.DyeInnerRadiusMm)
                {
                    // Clear polycarbonate hub: frosted clamping area, raised stacking ring.
                    c = r >= disc.StackingRingInnerRadiusMm && r < disc.StackingRingOuterRadiusMm ? (118, 120, 130)
                        : r >= disc.ClampingInnerRadiusMm && r < disc.ClampingOuterRadiusMm ? (98, 100, 108)
                        : (84, 86, 94);
                    c = Sheen(c, angle, r, 0.18);
                }
                else if (r > disc.DyeOuterRadiusMm)
                    c = Sheen((120, 125, 140), angle, r, 0.15); // clear outer rim
                else
                {
                    double level; // 1 = unburned dye, 0 = fully burned
                    if (r < disc.PowerCalibrationRadiusMm) level = 1.0;                 // unwritten dye
                    else if (r < disc.LeadInRadiusMm) level = 0.75;                      // PCA/PMA test marks
                    else if (r < disc.ProgramStartRadiusMm) level = WrittenTexture(r);   // lead-in
                    else if (r < disc.ProgramEndRadiusMm)
                        level = r < drawInner || r > drawOuter ? WrittenTexture(r)       // songs / files
                            : SampleWarp(warp, warpSize, warpMmPerPixel, dx, dy) / 255.0; // the art
                    else if (r < disc.LeadOutEndRadiusMm) level = WrittenTexture(r);     // lead-out
                    else level = 1.0;                                                    // unwritten dye
                    c = Sheen(Dye(level), angle, r, 0.35);
                }
                int o = (y * size + x) * 4;
                pixels[o] = ToByte(c.B); pixels[o + 1] = ToByte(c.G); pixels[o + 2] = ToByte(c.R); pixels[o + 3] = 255;
            }
        });
        return pixels;
    }

    /// <summary>Ordinary data reads as an even mid-tone with faint track banding.</summary>
    private static double WrittenTexture(double r) => 0.5 + 0.07 * Math.Sin(r * 9.0);

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

    private static (double, double, double) Sheen((double R, double G, double B) c, double angle, double r,
        double strength)
    {
        double hue = angle * 2 + r * 0.08;
        double sheen = Math.Pow(Math.Cos(2 * (angle - 0.6)), 8) * strength;
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

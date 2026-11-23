using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DiscImageStudio.Cd;

namespace MixtapeCd.App;

/// <summary>Draws labelled diameter rings over the to-scale disc render.</summary>
internal static class DiscDimensions
{
    internal static Bitmap Draw(Bitmap disc, CdPhysicalLayout layout,
        IEnumerable<(double RadiusMm, string Label)> extra)
    {
        int size = disc.PixelSize.Width;
        double c = size / 2.0, pxPerMm = size / (2 * layout.DiscRadiusMm);
        List<(double Radius, string Label)> rings =
        [
            (layout.CenterHoleRadiusMm, "centre hole"),
            (layout.DyeInnerRadiusMm, "dye starts"),
            (layout.ProgramStartRadiusMm, "data starts"),
            .. extra,
            (layout.ProgramEndRadiusMm, "data ends"),
            (layout.DiscRadiusMm, "disc edge"),
        ];

        rings.Sort((a, b) => a.Radius.CompareTo(b.Radius));
        RenderTargetBitmap view = new(disc.PixelSize, new Vector(96, 96));
        using DrawingContext context = view.CreateDrawingContext();
        context.DrawImage(disc, new Rect(0, 0, size, size));
        IBrush ink = new SolidColorBrush(Color.FromRgb(255, 214, 102));
        IBrush dark = new SolidColorBrush(Color.FromRgb(17, 18, 26));
        Pen ring = new(ink, Math.Max(1.2, size / 600.0), new DashStyle([5, 4], 0));
        Typeface typeface = new("Inter", FontStyle.Normal, FontWeight.Bold);
        double badge = Math.Max(9, size / 55.0);

        StringBuilderLegend legend = new();
        for (int i = 0; i < rings.Count; i++)
        {
            (double radius, string label) = rings[i];
            double r = radius * pxPerMm;
            context.DrawEllipse(null, ring, new Point(c, c), r, r);
            // Numbered badge on the ring; each ring gets its own angle so close rings don't collide.
            double angle = (-140 + 32 * i) * Math.PI / 180;
            Point at = new(c + r * Math.Cos(angle), c + r * Math.Sin(angle));
            context.DrawEllipse(ink, new Pen(dark, 1.5), at, badge, badge);
            FormattedText number = new($"{i + 1}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, badge * 1.25, dark);
            context.DrawText(number, new Point(at.X - number.Width / 2, at.Y - number.Height / 2));
            legend.Add(i + 1, 2 * radius, label);
        }
        Legend = legend.ToString();
        return view;
    }

    /// <summary>Text key for the badges of the last drawing.</summary>
    internal static string Legend { get; private set; } = "";

    private sealed class StringBuilderLegend
    {
        private readonly List<string> _items = [];
        internal void Add(int number, double diameter, string label) => _items.Add($"{number} {label} Ø{diameter:0.0} mm");
        public override string ToString() => string.Join("   ·   ", _items);
    }
}

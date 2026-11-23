using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MixtapeCd.App;

/// <summary>
/// Turns any uploaded picture (PNG, JPEG, WebP, BMP, GIF…) into the square, disc-centred image the
/// engine expects, and draws the "your image" view with the unused areas dimmed.
/// </summary>
internal static class ArtPrep
{
    private const int MaxSide = 2048;
    private static readonly string CacheDirectory = Path.Combine(Path.GetTempPath(), "MixtapeCD", "art");

    /// <summary>Square PNG for the engine. <paramref name="fit"/>: whole image with black borders;
    /// otherwise the centre square fills the disc.</summary>
    internal static (string Path, Bitmap Square) PrepareSquare(string source, bool fit)
    {
        FileInfo file = new(source);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{fit}")))[..24];
        Directory.CreateDirectory(CacheDirectory);
        string output = Path.Combine(CacheDirectory, key + ".png");
        if (File.Exists(output)) return (output, new Bitmap(output));

        using Bitmap image = new(source);
        double w = image.PixelSize.Width, h = image.PixelSize.Height;
        int side = (int)Math.Min(MaxSide, fit ? Math.Max(w, h) : Math.Min(w, h));
        double scale = side / (fit ? Math.Max(w, h) : Math.Min(w, h));
        Rect destination = new((side - w * scale) / 2, (side - h * scale) / 2, w * scale, h * scale);

        RenderTargetBitmap square = new(new PixelSize(side, side), new Vector(96, 96));
        using (DrawingContext context = square.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.Black, new Rect(0, 0, side, side));
            context.DrawImage(image, new Rect(0, 0, w, h), destination);
        }
        square.Save(output);
        return (output, square);
    }

    /// <summary>
    /// The square image with everything that won't appear on the disc dimmed: outside the disc,
    /// the hub, and the song area. Radii are fractions of the square's half-width.
    /// </summary>
    internal static Bitmap RenderSourceView(Bitmap square, int size, double hubFraction,
        double artInnerFraction, double artOuterFraction)
    {
        RenderTargetBitmap view = new(new PixelSize(size, size), new Vector(96, 96));
        double c = size / 2.0;
        Rect all = new(0, 0, size, size);
        EllipseGeometry Circle(double fraction) => new(new Rect(c - c * fraction, c - c * fraction,
            2 * c * fraction, 2 * c * fraction));
        IBrush dim = new SolidColorBrush(Color.FromArgb(190, 17, 18, 26));
        IBrush songs = new SolidColorBrush(Color.FromArgb(170, 74, 90, 158));
        Pen outline = new(new SolidColorBrush(Color.FromArgb(220, 201, 207, 230)), 1.5);
        Pen dashed = new(new SolidColorBrush(Color.FromArgb(220, 201, 207, 230)), 1.2, new DashStyle([4, 4], 0));

        using DrawingContext context = view.CreateDrawingContext();
        context.DrawImage(square, new Rect(0, 0, square.PixelSize.Width, square.PixelSize.Height), all);
        // Outside the disc and the centre hub never carry art.
        context.DrawGeometry(dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), Circle(1)));
        context.DrawGeometry(dim, null, Circle(hubFraction));
        // Song area: inner ring (audio CD) or outer band (MP3 disc).
        if (artInnerFraction > hubFraction + 0.002)
            context.DrawGeometry(songs, null, new CombinedGeometry(GeometryCombineMode.Exclude, Circle(artInnerFraction), Circle(hubFraction)));
        if (artOuterFraction < 0.998)
            context.DrawGeometry(songs, null, new CombinedGeometry(GeometryCombineMode.Exclude, Circle(1), Circle(artOuterFraction)));
        context.DrawGeometry(null, outline, Circle(1));
        context.DrawGeometry(null, outline, Circle(hubFraction));
        if (artInnerFraction > hubFraction + 0.002) context.DrawGeometry(null, dashed, Circle(artInnerFraction));
        if (artOuterFraction < 0.998) context.DrawGeometry(null, dashed, Circle(artOuterFraction));
        return view;
    }
}

using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland.Helpers;

/// <summary>
/// Picks the dominant vivid colour of an album cover, used to tint the equalizer and progress bar the way iOS
/// tints its waveform. Works on a 24×24 thumbnail (576 pixels), so it costs well under a millisecond per track.
/// </summary>
internal static class ColorExtractor
{
    private const int SampleSize = 24;
    private const int HueBuckets = 12; // 30° slices of the colour wheel

    public static Color? GetAccentColor(BitmapSource image)
    {
        try
        {
            var small = new TransformedBitmap(image, new ScaleTransform(
                (double)SampleSize / image.PixelWidth, (double)SampleSize / image.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);

            int stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);

            // Weighted colour sums per hue bucket. Weight = saturation × brightness, so greys and near-blacks
            // (album borders, dark backgrounds) barely count and the vivid "theme" colour wins.
            var weight = new double[HueBuckets];
            var red = new double[HueBuckets];
            var green = new double[HueBuckets];
            var blue = new double[HueBuckets];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double delta = max - min;
                if (max < 40 || delta < 25)
                    continue; // too dark or too grey to be an accent

                double hue = max == r ? (g - b) / delta + (g < b ? 6 : 0)
                           : max == g ? (b - r) / delta + 2
                           : (r - g) / delta + 4; // 0..6
                int bucket = Math.Min(HueBuckets - 1, (int)(hue / 6 * HueBuckets));

                double w = delta / max * (max / 255.0);
                weight[bucket] += w;
                red[bucket] += r * w;
                green[bucket] += g * w;
                blue[bucket] += b * w;
            }

            int best = 0;
            for (int k = 1; k < HueBuckets; k++)
                if (weight[k] > weight[best]) best = k;
            if (weight[best] < 0.5)
                return null; // black-and-white cover: keep the default white

            double cr = red[best] / weight[best], cg = green[best] / weight[best], cb = blue[best] / weight[best];

            // Lift the brightness so the colour reads clearly on the black island.
            double lift = 225 / Math.Max(cr, Math.Max(cg, cb));
            if (lift > 1)
            {
                cr *= lift;
                cg *= lift;
                cb *= lift;
            }

            return Color.FromRgb(ToByte(cr), ToByte(cg), ToByte(cb));
        }
        catch
        {
            return null;
        }
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

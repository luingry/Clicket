using System;

namespace Clicket.Core;

/// <summary>A 32-bit BGRA picture in memory, top row first. Alpha is ignored.</summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] pixels, int stride)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (stride < width * 4 || pixels.Length < stride * (height - 1) + width * 4) throw new ArgumentException("Pixel buffer too small.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
        Stride = stride;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    /// <summary>A copy of a rectangle, clipped to the picture; null when nothing is left.</summary>
    public PixelImage? Crop(int x, int y, int width, int height)
    {
        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = Math.Min(Width, x + width);
        var bottom = Math.Min(Height, y + height);
        if (right - left < 1 || bottom - top < 1) return null;
        var w = right - left;
        var h = bottom - top;
        var copy = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Buffer.BlockCopy(Pixels, (top + row) * Stride + left * 4, copy, row * w * 4, w * 4);
        return new PixelImage(w, h, copy, w * 4);
    }
}

/// <summary>Where the best match is (top-left, in the searched picture's pixels) and how alike it is, 0–1.</summary>
public readonly struct ImageMatch
{
    public ImageMatch(bool found, int x, int y, double score)
    {
        Found = found;
        X = x;
        Y = y;
        Score = score;
    }

    public bool Found { get; }
    public int X { get; }
    public int Y { get; }
    public double Score { get; }
}

/// <summary>
/// Finds a picture inside a larger one (the screen) at its exact pixel size.
/// <para>
/// The score is the normalized cross-correlation of brightness, so a pattern still matches when it is a little lighter or
/// darker, capped by how close the average colour is, so a red button does not match the same button in green. A picture of
/// one flat colour has no pattern; it scores by colour and evenness instead.
/// </para>
/// <para>
/// Speed comes from a pyramid: every position is scored on copies shrunk 2–16×, the best few candidates are followed down
/// level by level within ±2 pixels, and only those are scored at full size. A search of a 1440p screen takes tens of
/// milliseconds. One instance belongs to one thread; it keeps its buffers between searches.
/// </para>
/// </summary>
public sealed class ImageMatcher
{
    private const int MaxLevels = 4, Candidates = 16, Reach = 2;
    private const double FlatStd = 2.0, MinCoarseScore = 0.15, CoarseSlack = 0.4;

    private readonly PixelImage _template;
    private readonly int _levels;
    private readonly int[] _tw, _th;
    private readonly float[][] _tCentered;
    private readonly double[] _tMean, _tVar;
    private readonly bool _flat;
    private readonly double _meanB, _meanG, _meanR;

    private float[][] _sGray = new float[0][];
    private int[] _sw = new int[0], _sh = new int[0];
    private double[] _sum = new double[0], _sumSq = new double[0];

    public ImageMatcher(PixelImage template)
    {
        _template = template;
        var w = template.Width;
        var h = template.Height;

        // Shrink while the smallest level keeps a usable pattern: at least 4 px a side and 48 px in all.
        var levels = 0;
        while (levels < MaxLevels && (w >> (levels + 1)) >= 4 && (h >> (levels + 1)) >= 4 && (w >> (levels + 1)) * (h >> (levels + 1)) >= 48) levels++;
        _levels = levels;

        _tw = new int[levels + 1];
        _th = new int[levels + 1];
        _tCentered = new float[levels + 1][];
        _tMean = new double[levels + 1];
        _tVar = new double[levels + 1];

        var gray = ToGray(template, out _meanB, out _meanG, out _meanR);
        _tw[0] = w;
        _th[0] = h;
        for (var level = 0; level <= levels; level++)
        {
            if (level > 0)
            {
                _tw[level] = _tw[level - 1] / 2;
                _th[level] = _th[level - 1] / 2;
                gray = Half(gray, _tw[level - 1], _th[level - 1], null);
            }
            var n = _tw[level] * _th[level];
            double sum = 0;
            for (var i = 0; i < n; i++) sum += gray[i];
            var mean = sum / n;
            var centered = new float[n];
            double variance = 0;
            for (var i = 0; i < n; i++)
            {
                centered[i] = (float)(gray[i] - mean);
                variance += centered[i] * (double)centered[i];
            }
            _tCentered[level] = centered;
            _tMean[level] = mean;
            _tVar[level] = variance;
        }
        _flat = MaxChannelStd(template, _meanB, _meanG, _meanR) < FlatStd;
    }

    private static double MaxChannelStd(PixelImage image, double meanB, double meanG, double meanR)
    {
        double vb = 0, vg = 0, vr = 0;
        for (var y = 0; y < image.Height; y++)
        {
            var offset = y * image.Stride;
            for (var x = 0; x < image.Width; x++, offset += 4)
            {
                vb += (image.Pixels[offset] - meanB) * (image.Pixels[offset] - meanB);
                vg += (image.Pixels[offset + 1] - meanG) * (image.Pixels[offset + 1] - meanG);
                vr += (image.Pixels[offset + 2] - meanR) * (image.Pixels[offset + 2] - meanR);
            }
        }
        var n = (double)image.Width * image.Height;
        return Math.Sqrt(Math.Max(vb, Math.Max(vg, vr)) / n);
    }

    public int Width => _template.Width;

    public int Height => _template.Height;

    /// <summary>The best match in <paramref name="screen"/>; <see cref="ImageMatch.Found"/> when its score reaches <paramref name="threshold"/> (0–1).</summary>
    public ImageMatch Find(PixelImage screen, double threshold)
    {
        if (screen.Width < _template.Width || screen.Height < _template.Height) return new ImageMatch(false, 0, 0, 0);
        PrepareScreen(screen);
        var top = Math.Min(_levels, _sGray.Length - 1);
        while (top > 0 && (_sw[top] < _tw[top] || _sh[top] < _th[top])) top--;

        // 1. Every position on the smallest level.
        var candidates = new Candidate[Candidates];
        var count = CoarseScan(top, Math.Max(MinCoarseScore, threshold - CoarseSlack), candidates);

        // 2. Follow each candidate down, then score it at full size with colour.
        var best = new ImageMatch(false, 0, 0, double.NegativeInfinity);
        for (var c = 0; c < count; c++)
        {
            var x = candidates[c].X;
            var y = candidates[c].Y;
            for (var level = top - 1; level >= 0; level--) Refine(level, x * 2, y * 2, out x, out y);
            var score = FinalScore(screen, x, y);
            if (score > best.Score) best = new ImageMatch(false, x, y, score);
        }
        if (double.IsNegativeInfinity(best.Score)) return new ImageMatch(false, 0, 0, 0);
        return new ImageMatch(best.Score >= threshold, best.X, best.Y, Math.Max(0, best.Score));
    }

    private struct Candidate
    {
        public int X, Y;
        public double Score;
    }

    private int CoarseScan(int level, double minimum, Candidate[] list)
    {
        var sw = _sw[level];
        var sh = _sh[level];
        var tw = _tw[level];
        var th = _th[level];
        var s = _sGray[level];
        var t = _tCentered[level];
        var n = tw * th;
        BuildIntegrals(s, sw, sh);
        var stride = sw + 1;
        var count = 0;
        var floor = minimum;
        var nearX = Math.Max(1, tw / 2);
        var nearY = Math.Max(1, th / 2);

        for (var y = 0; y <= sh - th; y++)
        {
            for (var x = 0; x <= sw - tw; x++)
            {
                var a = y * stride + x;
                var b = a + tw;
                var c = a + th * stride;
                var d = c + tw;
                var sum = _sum[d] - _sum[b] - _sum[c] + _sum[a];
                var sumSq = _sumSq[d] - _sumSq[b] - _sumSq[c] + _sumSq[a];
                double score;
                if (_flat)
                {
                    score = FlatScore(sum, sumSq, n);
                }
                else
                {
                    var variance = sumSq - sum * sum / n;
                    if (variance < n) continue; // a flat window has no pattern to correlate
                    double cross = 0;
                    for (var j = 0; j < th; j++)
                    {
                        var row = (y + j) * sw + x;
                        var trow = j * tw;
                        for (var i = 0; i < tw; i++) cross += s[row + i] * t[trow + i];
                    }
                    score = cross / Math.Sqrt(variance * _tVar[level]);
                }
                if (score < floor) continue;
                count = Offer(list, count, x, y, score, nearX, nearY);
                if (count == list.Length) floor = Math.Max(minimum, Lowest(list, count));
            }
        }
        return count;
    }

    /// <summary>Keeps the best few, one per neighbourhood, so one strong match cannot crowd out every other place.</summary>
    private static int Offer(Candidate[] list, int count, int x, int y, double score, int nearX, int nearY)
    {
        for (var i = 0; i < count; i++)
        {
            if (Math.Abs(list[i].X - x) >= nearX || Math.Abs(list[i].Y - y) >= nearY) continue;
            if (score > list[i].Score) list[i] = new Candidate { X = x, Y = y, Score = score };
            return count;
        }
        if (count < list.Length)
        {
            list[count] = new Candidate { X = x, Y = y, Score = score };
            return count + 1;
        }
        var worst = 0;
        for (var i = 1; i < count; i++) if (list[i].Score < list[worst].Score) worst = i;
        if (score > list[worst].Score) list[worst] = new Candidate { X = x, Y = y, Score = score };
        return count;
    }

    private static double Lowest(Candidate[] list, int count)
    {
        var low = double.PositiveInfinity;
        for (var i = 0; i < count; i++) low = Math.Min(low, list[i].Score);
        return low;
    }

    private void Refine(int level, int cx, int cy, out int bestX, out int bestY)
    {
        bestX = Math.Max(0, Math.Min(cx, _sw[level] - _tw[level]));
        bestY = Math.Max(0, Math.Min(cy, _sh[level] - _th[level]));
        var best = double.NegativeInfinity;
        for (var dy = -Reach; dy <= Reach; dy++)
        {
            for (var dx = -Reach; dx <= Reach; dx++)
            {
                var x = cx + dx;
                var y = cy + dy;
                if (x < 0 || y < 0 || x > _sw[level] - _tw[level] || y > _sh[level] - _th[level]) continue;
                var score = GrayScore(level, x, y);
                if (score <= best) continue;
                best = score;
                bestX = x;
                bestY = y;
            }
        }
    }

    private double GrayScore(int level, int x, int y)
    {
        var sw = _sw[level];
        var tw = _tw[level];
        var th = _th[level];
        var s = _sGray[level];
        var t = _tCentered[level];
        var n = tw * th;
        double sum = 0, sumSq = 0, cross = 0;
        for (var j = 0; j < th; j++)
        {
            var row = (y + j) * sw + x;
            var trow = j * tw;
            for (var i = 0; i < tw; i++)
            {
                double v = s[row + i];
                sum += v;
                sumSq += v * v;
                cross += v * t[trow + i];
            }
        }
        if (_flat) return FlatScore(sum, sumSq, n);
        var variance = sumSq - sum * sum / n;
        if (variance < n) return 0;
        return cross / Math.Sqrt(variance * _tVar[level]);
    }

    /// <summary>Brightness pattern at full size, capped by the closeness of the average colour.</summary>
    private double FinalScore(PixelImage screen, int x, int y)
    {
        var pattern = GrayScore(0, x, y);
        double b = 0, g = 0, r = 0;
        var w = _template.Width;
        var h = _template.Height;
        var px = screen.Pixels;
        for (var j = 0; j < h; j++)
        {
            var offset = (y + j) * screen.Stride + x * 4;
            for (var i = 0; i < w; i++, offset += 4)
            {
                b += px[offset];
                g += px[offset + 1];
                r += px[offset + 2];
            }
        }
        var n = (double)w * h;
        var drift = Math.Max(Math.Abs(b / n - _meanB), Math.Max(Math.Abs(g / n - _meanG), Math.Abs(r / n - _meanR)));
        return Math.Min(pattern, 1 - drift / 255);
    }

    /// <summary>
    /// For a one-colour picture the screen levels hold each pixel's distance to that colour instead of brightness:
    /// the window must be close to it on average and evenly so.
    /// </summary>
    private static double FlatScore(double sum, double sumSq, int n)
    {
        var mean = sum / n;
        var spread = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
        return 1 - (mean + spread) / 255;
    }

    private void PrepareScreen(PixelImage screen)
    {
        var levels = _levels + 1;
        if (_sGray.Length != levels || _sw[0] != screen.Width || _sh[0] != screen.Height)
        {
            _sGray = new float[levels][];
            _sw = new int[levels];
            _sh = new int[levels];
            _sw[0] = screen.Width;
            _sh[0] = screen.Height;
            for (var level = 1; level < levels; level++)
            {
                _sw[level] = _sw[level - 1] / 2;
                _sh[level] = _sh[level - 1] / 2;
            }
            for (var level = 0; level < levels; level++) _sGray[level] = new float[Math.Max(1, _sw[level] * _sh[level])];
        }
        if (_flat) FillDistance(screen, _sGray[0]);
        else FillGray(screen, _sGray[0]);
        for (var level = 1; level < levels; level++)
        {
            if (_sw[level] < 1 || _sh[level] < 1) break;
            Half(_sGray[level - 1], _sw[level - 1], _sh[level - 1], _sGray[level]);
        }
    }

    private void BuildIntegrals(float[] s, int w, int h)
    {
        var size = (w + 1) * (h + 1);
        if (_sum.Length < size)
        {
            _sum = new double[size];
            _sumSq = new double[size];
        }
        var stride = w + 1;
        for (var x = 0; x <= w; x++)
        {
            _sum[x] = 0;
            _sumSq[x] = 0;
        }
        for (var y = 1; y <= h; y++)
        {
            double row = 0, rowSq = 0;
            _sum[y * stride] = 0;
            _sumSq[y * stride] = 0;
            var src = (y - 1) * w;
            for (var x = 1; x <= w; x++)
            {
                double v = s[src + x - 1];
                row += v;
                rowSq += v * v;
                _sum[y * stride + x] = _sum[(y - 1) * stride + x] + row;
                _sumSq[y * stride + x] = _sumSq[(y - 1) * stride + x] + rowSq;
            }
        }
    }

    private static float[] ToGray(PixelImage image, out double meanB, out double meanG, out double meanR)
    {
        var gray = new float[image.Width * image.Height];
        double b = 0, g = 0, r = 0;
        var px = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var offset = y * image.Stride;
            for (var x = 0; x < image.Width; x++, offset += 4)
            {
                b += px[offset];
                g += px[offset + 1];
                r += px[offset + 2];
                gray[y * image.Width + x] = (px[offset] * 29 + px[offset + 1] * 150 + px[offset + 2] * 77) >> 8;
            }
        }
        var n = (double)gray.Length;
        meanB = b / n;
        meanG = g / n;
        meanR = r / n;
        return gray;
    }

    private static void FillGray(PixelImage image, float[] gray)
    {
        var px = image.Pixels;
        var w = image.Width;
        for (var y = 0; y < image.Height; y++)
        {
            var offset = y * image.Stride;
            var row = y * w;
            for (var x = 0; x < w; x++, offset += 4) gray[row + x] = (px[offset] * 29 + px[offset + 1] * 150 + px[offset + 2] * 77) >> 8;
        }
    }

    /// <summary>Largest per-channel difference from the picture's colour, 0 (same colour) to 255.</summary>
    private void FillDistance(PixelImage image, float[] distance)
    {
        var px = image.Pixels;
        var w = image.Width;
        var b = (int)Math.Round(_meanB);
        var g = (int)Math.Round(_meanG);
        var r = (int)Math.Round(_meanR);
        for (var y = 0; y < image.Height; y++)
        {
            var offset = y * image.Stride;
            var row = y * w;
            for (var x = 0; x < w; x++, offset += 4)
                distance[row + x] = Math.Max(Math.Abs(px[offset] - b), Math.Max(Math.Abs(px[offset + 1] - g), Math.Abs(px[offset + 2] - r)));
        }
    }

    /// <summary>2×2 box average; odd edges are dropped, so level coordinates stay exact halves.</summary>
    private static float[] Half(float[] src, int w, int h, float[]? dst)
    {
        var w2 = w / 2;
        var h2 = h / 2;
        dst ??= new float[Math.Max(1, w2 * h2)];
        for (var y = 0; y < h2; y++)
        {
            var a = 2 * y * w;
            var b = a + w;
            var o = y * w2;
            for (var x = 0; x < w2; x++)
            {
                var i = 2 * x;
                dst[o + x] = (src[a + i] + src[a + i + 1] + src[b + i] + src[b + i + 1]) * 0.25f;
            }
        }
        return dst;
    }
}

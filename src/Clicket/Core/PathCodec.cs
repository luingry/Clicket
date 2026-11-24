using System;
using System.Collections.Generic;
using System.IO;

namespace Clicket.Core;

/// <summary>One recorded pointer sample: physical screen pixels and microseconds since the path started.</summary>
public readonly struct PathPoint
{
    public PathPoint(int x, int y, long micros)
    {
        X = x;
        Y = y;
        Micros = micros;
    }

    public int X { get; }
    public int Y { get; }
    public long Micros { get; }
}

/// <summary>
/// Stores a recorded mouse path losslessly and compactly: every sample kept, as zigzag varints of the
/// deltas (x, y, time in microseconds), base64 in the file. A 1000 Hz mouse moving for 10 s fits in about 40 KB.
/// </summary>
public static class PathCodec
{
    private const byte Version = 1;

    public static string Encode(IReadOnlyList<PathPoint> points)
    {
        using var stream = new MemoryStream(points.Count * 4 + 8);
        stream.WriteByte(Version);
        WriteVarint(stream, (ulong)points.Count);
        int px = 0, py = 0;
        long pt = 0;
        foreach (var p in points)
        {
            WriteVarint(stream, ZigZag(p.X - px));
            WriteVarint(stream, ZigZag(p.Y - py));
            WriteVarint(stream, ZigZag(p.Micros - pt));
            px = p.X;
            py = p.Y;
            pt = p.Micros;
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>The samples, or an empty list for missing or damaged data (a damaged path never plays half-way wrong).</summary>
    public static List<PathPoint> Decode(string? data)
    {
        var points = new List<PathPoint>();
        if (string.IsNullOrEmpty(data)) return points;
        try
        {
            var bytes = Convert.FromBase64String(data);
            var index = 0;
            if (bytes.Length == 0 || bytes[index++] != Version) return points;
            var count = (long)ReadVarint(bytes, ref index);
            if (count < 0 || count > 50_000_000) return new List<PathPoint>();
            points.Capacity = (int)Math.Min(count, 1_000_000);
            int x = 0, y = 0;
            long t = 0;
            for (long i = 0; i < count; i++)
            {
                x += (int)UnZigZag(ReadVarint(bytes, ref index));
                y += (int)UnZigZag(ReadVarint(bytes, ref index));
                t += UnZigZag(ReadVarint(bytes, ref index));
                points.Add(new PathPoint(x, y, t));
            }
        }
        catch (Exception e) when (e is FormatException || e is IndexOutOfRangeException || e is OverflowException)
        {
            return new List<PathPoint>();
        }
        return points;
    }

    /// <summary>Count and duration without decoding into a list.</summary>
    public static (int Count, long Micros) Measure(string? data)
    {
        if (string.IsNullOrEmpty(data)) return (0, 0);
        // The list and the estimates ask often; the encoded string is immutable, so its measure is cached with it.
        var cached = MeasureCache.GetValue(data!, d =>
        {
            var points = Decode(d);
            return new Measured(points.Count, points.Count == 0 ? 0 : points[points.Count - 1].Micros);
        });
        return (cached.Count, cached.Micros);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, Measured> MeasureCache =
        new System.Runtime.CompilerServices.ConditionalWeakTable<string, Measured>();

    private sealed class Measured
    {
        public Measured(int count, long micros)
        {
            Count = count;
            Micros = micros;
        }

        public int Count { get; }
        public long Micros { get; }
    }

    private static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    private static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static ulong ReadVarint(byte[] bytes, ref int index)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            var b = bytes[index++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new OverflowException();
        }
    }
}

using System.Diagnostics;

namespace HKTools.Media;

/// <summary>Пики модуля сигнала с разрешением 1 мс и «мипмапами» для быстрой отрисовки.</summary>
public sealed class Envelope
{
    public readonly List<ushort[]> Levels = new();
    public readonly int Peak;

    public Envelope(ushort[] peaks)
    {
        Levels.Add(peaks);
        var cur = peaks;
        while (cur.Length > 2048)
        {
            var next = new ushort[(cur.Length + 3) / 4];
            for (int i = 0; i < next.Length; i++)
            {
                ushort m = 0;
                for (int j = i * 4; j < Math.Min(cur.Length, i * 4 + 4); j++) if (cur[j] > m) m = cur[j];
                next[i] = m;
            }
            Levels.Add(next);
            cur = next;
        }
        Peak = peaks.Length > 0 ? peaks.Max() : 1;
    }

    public int LengthMs => Levels[0].Length;

    public int WindowMax(double startMs, double endMs)
    {
        if (endMs <= 0 || startMs >= LengthMs) return 0;
        var span = Math.Max(1, endMs - startMs);
        int level = 0;
        while (level + 1 < Levels.Count && Math.Pow(4, level + 1) <= span) level++;
        var scale = Math.Pow(4, level);
        var data = Levels[level];
        int a = Math.Max(0, (int)(startMs / scale));
        int b = Math.Min(data.Length, Math.Max(a + 1, (int)Math.Ceiling(endMs / scale)));
        int m = 0;
        for (int i = a; i < b; i++) if (data[i] > m) m = data[i];
        return m;
    }
}

public static class Waveform
{
    const int Rate = 8000, Bin = 8; // 8 кГц, 8 сэмплов = 1 мс

    public static Envelope Compute(string path, int streamIndex, double? limitS, CancelToken token, Action<double> onProgress = null, double duration = 0)
    {
        var cmd = new List<string> { FFmpeg.Bin, "-hide_banner", "-nostdin", "-loglevel", "error", "-i", path, "-map", $"0:{streamIndex}" };
        if (limitS is > 0) cmd.AddRange(["-t", limitS.Value.ToString("0.000", FFmpeg.Inv)]);
        cmd.AddRange(["-ac", "1", "-ar", Rate.ToString(), "-f", "s16le", "-"]);
        var psi = new ProcessStartInfo(cmd[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        token.Attach(p);
        _ = p.StandardError.ReadToEndAsync();
        var peaks = new List<ushort>(1 << 20);
        var buf = new byte[Rate * 2 * 4];
        var total = (limitS ?? duration) * Rate * 2;
        long read = 0;
        int carry = 0;
        var stream = p.StandardOutput.BaseStream;
        int n;
        while ((n = stream.Read(buf, carry, buf.Length - carry)) > 0)
        {
            token.Check();
            read += n;
            int avail = carry + n;
            int usable = avail - avail % (Bin * 2);
            for (int i = 0; i < usable; i += Bin * 2)
            {
                int m = 0;
                for (int j = 0; j < Bin * 2; j += 2)
                {
                    int s = (short)(buf[i + j] | (buf[i + j + 1] << 8));
                    if (s < 0) s = -s;
                    if (s > m) m = s;
                }
                peaks.Add((ushort)Math.Min(65535, m));
            }
            carry = avail - usable;
            if (carry > 0) Array.Copy(buf, usable, buf, 0, carry);
            if (onProgress != null && total > 0) onProgress(Math.Min(100, read / total * 100));
        }
        p.WaitForExit();
        token.Detach(p);
        token.Check();
        return new Envelope(peaks.ToArray());
    }

    /// <summary>Сдвиг донора в мс, при котором огибающие совпадают лучше всего (+ = донор позже).</summary>
    public static int AutoOffset(Envelope reference, Envelope donor, int maxLagMs = 15000, int analyzeMs = 180_000)
    {
        const int step = 10;
        double[] Pool(Envelope e, int limit)
        {
            var b = e.Levels[0];
            int n = Math.Min(b.Length, limit);
            var r = new double[(n + step - 1) / step];
            for (int i = 0; i < r.Length; i++)
            {
                int m = 0;
                for (int j = i * step; j < Math.Min(n, i * step + step); j++) if (b[j] > m) m = b[j];
                r[i] = m;
            }
            var mean = r.Length > 0 ? r.Average() : 0;
            var sd = r.Length > 0 ? Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / r.Length) : 1;
            if (sd == 0) sd = 1;
            for (int i = 0; i < r.Length; i++) r[i] = (r[i] - mean) / sd;
            return r;
        }
        var A = Pool(reference, analyzeMs + maxLagMs);
        var B = Pool(donor, analyzeMs + maxLagMs);
        int lagN = maxLagMs / step, limitN = analyzeMs / step;
        double best = double.MinValue;
        int bestLag = 0;
        for (int lag = -lagN; lag <= lagN; lag++)
        {
            int ao = Math.Max(0, lag), bo = Math.Max(0, -lag);
            int n = Math.Min(Math.Min(A.Length - ao, B.Length - bo), limitN);
            if (n < 50) continue;
            double s = 0;
            for (int i = 0; i < n; i++) s += A[ao + i] * B[bo + i];
            s /= n;
            if (s > best) { best = s; bestLag = lag; }
        }
        int coarse = bestLag * step;
        var ra = reference.Levels[0];
        var rb = donor.Levels[0];
        int nn = Math.Min(Math.Min(ra.Length, rb.Length), analyzeMs);
        double bestF = double.MinValue;
        int fine = coarse;
        for (int lag = coarse - step; lag <= coarse + step; lag++)
        {
            double s = 0;
            int cnt = 0;
            for (int i = 0; i < nn; i += 3)
            {
                int j = i - lag;
                if (j >= 0 && j < rb.Length && i < ra.Length) { s += (double)ra[i] * rb[j]; cnt++; }
            }
            if (cnt > 0 && s / cnt > bestF) { bestF = s / cnt; fine = lag; }
        }
        return fine;
    }
}

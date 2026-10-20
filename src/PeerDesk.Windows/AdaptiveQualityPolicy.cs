using System;

namespace PeerDesk.Windows
{
    internal enum QualityMode { Auto = 0, Sharp = 1, Fast = 2 }

    internal sealed class QualitySnapshot
    {
        internal readonly int JpegQuality, MaxWidth, MaxHeight, IntervalMs, Level;
        internal QualitySnapshot(int quality, int width, int height, int interval, int level)
        { JpegQuality = quality; MaxWidth = width; MaxHeight = height; IntervalMs = interval; Level = level; }
    }

    // Full frames only. This never grants capture/input authority or changes the negotiated wire bounds.
    // Samples must belong to this live session; create a new instance when its epoch changes.
    internal sealed class AdaptiveQualityPolicy
    {
        private readonly object sync = new object();
        private QualityMode mode;
        private int level = 1, badSamples, goodSamples;
        private double encodeAverage, rttAverage, bytesAverage, ageAverage;
        private bool sampled;
        internal AdaptiveQualityPolicy(QualityMode initialMode = QualityMode.Auto) { SetMode(initialMode); }
        internal void SetMode(QualityMode value)
        {
            if (!Enum.IsDefined(typeof(QualityMode), value)) throw new ArgumentOutOfRangeException("value");
            lock (sync) { mode = value; level = 1; ResetSamples(); }
        }
        private void ResetSamples()
        { badSamples = goodSamples = 0; sampled = false; encodeAverage = rttAverage = bytesAverage = ageAverage = 0; }
        internal QualityMode Mode { get { lock (sync) return mode; } }
        internal QualitySnapshot Snapshot()
        {
            lock (sync)
            {
                int selected = mode == QualityMode.Sharp ? 0 : mode == QualityMode.Fast ? 3 : level;
                switch (selected)
                {
                    case 0: return new QualitySnapshot(85, 1920, 1080, 50, 0);
                    case 1: return new QualitySnapshot(70, 1600, 900, 50, 1);
                    case 2: return new QualitySnapshot(55, 1280, 720, 66, 2);
                    default: return new QualitySnapshot(40, 960, 540, 100, 3);
                }
            }
        }
        // A dropped expired frame still provides load feedback, never capture authority.
        internal void ObserveExpiredFrame()
        {
            lock (sync)
            {
                if (mode != QualityMode.Auto) return;
                level = Math.Min(3, level + 1);
                ResetSamples();
            }
        }
        internal void Observe(double encodeMs, double roundTripMs, int frameBytes, double queueAgeMs)
        {
            Validate(encodeMs, 60000, "encodeMs");
            Validate(roundTripMs, 60000, "roundTripMs");
            Validate(queueAgeMs, 60000, "queueAgeMs");
            if (frameBytes < 1 || frameBytes > 524288) throw new ArgumentOutOfRangeException("frameBytes");
            lock (sync)
            {
                if (mode != QualityMode.Auto) return;
                if (!sampled)
                { encodeAverage = encodeMs; rttAverage = roundTripMs; bytesAverage = frameBytes; ageAverage = queueAgeMs; sampled = true; }
                else
                {
                    const double a = 0.25;
                    encodeAverage += a * (encodeMs - encodeAverage);
                    rttAverage += a * (roundTripMs - rttAverage);
                    bytesAverage += a * (frameBytes - bytesAverage);
                    ageAverage += a * (queueAgeMs - ageAverage);
                }
                bool overloaded = encodeAverage > 55 || rttAverage > 180 || ageAverage > 100 || bytesAverage > 400000;
                bool healthy = encodeAverage < 25 && rttAverage < 90 && ageAverage < 25 && bytesAverage < 180000;
                if (overloaded)
                {
                    goodSamples = 0;
                    if (++badSamples >= 3) { if (level < 3) level++; ResetSamples(); }
                }
                else if (healthy)
                {
                    badSamples = 0;
                    if (++goodSamples >= 30) { if (level > 0) level--; ResetSamples(); }
                }
                else { badSamples = goodSamples = 0; }
            }
        }
        private static void Validate(double value, double max, string name)
        { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value > max) throw new ArgumentOutOfRangeException(name); }
    }
}

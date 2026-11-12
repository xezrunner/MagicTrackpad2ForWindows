using System;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// A two-axis displacement.
    /// </summary>
    internal readonly record struct Pair(double X, double Y)
    {
        internal Pair Read() => new Pair((float)X, (float)Y);

        internal bool Any => X != 0 || Y != 0;
    }

    /// <summary>
    /// One pointer report admitted to the queue.
    /// </summary>
    internal readonly record struct PointerReport(double Arrival, double EventTime, double Interval,
        Pair Raw, Pair Scaled, ulong Sender = 1, int Buttons = 0, bool StateChanged = false, bool Flag20 = false);

    /// <summary>
    /// A displacement released by the queue.
    /// </summary>
    internal readonly record struct Delivery(double Time, Pair Raw, Pair Scaled, ulong Sender, int Buttons, string Reason);

    /// <summary>
    /// Distributes queued pointer displacement across display ticks.
    /// </summary>
    internal sealed class ExternalQueue
    {
        internal const int Capacity = 4096;

        internal struct Item
        {
            internal PointerReport Report;
            internal double Interval;
            internal double LastTime;
            internal double NextVbl;
            internal Pair ReferenceRaw;
            internal Pair ReferenceScaled;
            internal Pair RemainingRaw;
            internal Pair RemainingScaled;
            internal bool StateChanged;
            internal bool AllDeltas;
        }

        internal readonly Item[] Items = new Item[Capacity];
        internal int Count;
        internal Pair PreviousScaled;
        internal double PreviousEventTime;
        internal ulong? ActiveSender;
        internal double? NextDeadline;

        internal void Clear()
        {
            Count = 0;
            PreviousScaled = default;
            PreviousEventTime = 0;
            ActiveSender = null;
            NextDeadline = null;
        }

        internal static (double Portion, double Remainder) SplitAxis(double reference, double remaining, double fraction)
        {
            if (fraction == 1)
                return (remaining, 0);

            if (Math.Abs(remaining) <= 0.001)
                return (0, 0);

            double wanted = fraction * reference;
            double portion = Math.CopySign(Math.Min(Math.Abs(wanted), Math.Abs(remaining)), remaining);
            if (Math.Abs(remaining - portion) <= 0.001)
                return (remaining, 0);

            double rest = remaining - portion;
            if (Math.Abs(portion) <= 0.001 || wanted * rest > 0)
                return (portion, rest);

            return (remaining, 0);
        }

        internal Pair Push(PointerReport report, double nextVbl, Action<Delivery> deliver)
        {
            if (!double.IsFinite(report.Arrival) || !double.IsFinite(report.EventTime) ||
                !double.IsFinite(report.Interval) || report.Interval < 0 ||
                (!report.Flag20 && (!float.IsFinite((float)report.Interval) || (float)report.Interval <= 0)) ||
                !float.IsFinite((float)report.Raw.X) || !float.IsFinite((float)report.Raw.Y) ||
                !float.IsFinite((float)report.Scaled.X) || !float.IsFinite((float)report.Scaled.Y))
                throw new ArgumentOutOfRangeException(nameof(report));

            Pair raw = report.Raw.Read();
            Pair scaled = report.Scaled.Read();
            bool otherSender = ActiveSender.HasValue && ActiveSender != 0 && report.Sender != ActiveSender;
            if (otherSender || (Count == 0 && (report.StateChanged || report.Flag20)))
            {
                deliver(new Delivery(report.Arrival, raw, scaled, report.Sender, report.Buttons,
                    otherSender ? "other sender" : "empty transition"));
                return scaled;
            }

            if (!float.IsFinite((float)report.Interval))
                throw new ArgumentOutOfRangeException(nameof(report));

            if (Count == Capacity)
                throw new InvalidOperationException("Motion queue overflow");

            if (!report.Flag20)
            {
                // History reset uses the original interval, before its float32 narrowing.
                if (report.EventTime - PreviousEventTime > 3 * report.Interval)
                    PreviousScaled = default;

                double current = Math.Max(1.0 / 65536, Math.Sqrt(scaled.X * scaled.X + scaled.Y * scaled.Y));
                double previous = Math.Max(1.0 / 65536,
                    Math.Sqrt(PreviousScaled.X * PreviousScaled.X + PreviousScaled.Y * PreviousScaled.Y));
                double gain = Math.Max(0, (current + 0.5 * (current - previous)) / current);
                PreviousScaled = scaled;
                PreviousEventTime = report.EventTime;
                scaled = new Pair(scaled.X * gain, scaled.Y * gain);
                if (Count > 0 && Math.Abs((float)(Items[Count - 1].Report.Arrival - report.Arrival)) < (float)0.005)
                    Items[Count - 1].AllDeltas = true;
            }

            Items[Count++] = new Item
            {
                Report = report,
                Interval = report.Flag20 ? 0 : (float)report.Interval,
                LastTime = report.Arrival,
                NextVbl = nextVbl,
                ReferenceRaw = raw,
                ReferenceScaled = scaled,
                RemainingRaw = raw,
                RemainingScaled = scaled,
                StateChanged = report.StateChanged || report.Flag20,
                AllDeltas = report.Flag20
            };
            ActiveSender = report.Sender;
            NextDeadline ??= nextVbl;
            return scaled;
        }

        internal void Tick(double now, double nextVbl, Action<Delivery> deliver)
        {
            NextDeadline = null;
            if (Count == 0)
                return;

            bool transition = false;
            for (int i = 0; i < Count; i++)
                transition |= Items[i].StateChanged;

            if (transition)
            {
                for (int i = 0; i < Count; i++)
                {
                    ref Item item = ref Items[i];
                    deliver(new Delivery(now, item.RemainingRaw.Read(), item.RemainingScaled.Read(),
                        item.Report.Sender, item.Report.Buttons, "transition flush"));
                }
                Count = 0;
            }
            else
            {
                PointerReport first = Items[0].Report;
                double rx = 0;
                double ry = 0;
                double sx = 0;
                double sy = 0;
                int pending = 0;
                for (int i = 0; i < Count; i++)
                {
                    Item item = Items[i];
                    double elapsed = now - item.LastTime;
                    double fraction = item.AllDeltas || elapsed <= 0 ? 1 : Math.Min(elapsed / item.Interval, 1);
                    Pair raw = item.RemainingRaw.Read();
                    Pair scaled = item.RemainingScaled.Read();

                    (double Portion, double Remainder) x = SplitAxis(item.ReferenceRaw.X, raw.X, fraction);
                    (double Portion, double Remainder) y = SplitAxis(item.ReferenceRaw.Y, raw.Y, fraction);
                    rx += x.Portion;
                    ry += y.Portion;
                    item.RemainingRaw = new Pair(x.Remainder, y.Remainder);

                    x = SplitAxis(item.ReferenceScaled.X, scaled.X, fraction);
                    y = SplitAxis(item.ReferenceScaled.Y, scaled.Y, fraction);
                    sx += x.Portion;
                    sy += y.Portion;
                    item.RemainingScaled = new Pair(x.Remainder, y.Remainder);

                    item.LastTime = now;
                    if (item.RemainingRaw.Any || item.RemainingScaled.Any)
                        Items[pending++] = item;
                }
                Count = pending;
                deliver(new Delivery(now, new Pair(rx, ry).Read(), new Pair(sx, sy).Read(),
                    first.Sender, first.Buttons, "distributed"));
            }

            if (Count != 0)
                NextDeadline = nextVbl;
            else
                ActiveSender = null;
        }
    }
}

using System;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    internal readonly record struct Pair(double X, double Y)
    {
        internal Pair Read() => new Pair((float)X, (float)Y);

        internal bool Any => X != 0 || Y != 0;
    }

    /// <summary>
    /// Distributes queued pointer displacement across display ticks.
    /// </summary>
    internal sealed class ExternalQueue
    {
        internal const int Capacity = 4096;

        internal struct Item
        {
            internal double Arrival;
            internal double Interval;
            internal double LastTime;
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

        internal void Clear()
        {
            Count = 0;
            PreviousScaled = default;
            PreviousEventTime = 0;
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

        internal void Push(double arrival, double eventTime, double interval, Pair delta, bool stateChanged, Action<Pair> deliver)
        {
            Pair raw = delta.Read();
            Pair scaled = raw;
            if (Count == 0 && stateChanged)
            {
                deliver(scaled);
                return;
            }

            if (Count == Capacity)
                throw new InvalidOperationException("Motion queue overflow");

            // History reset uses the original interval, before its float32 narrowing.
            if (eventTime - PreviousEventTime > 3 * interval)
                PreviousScaled = default;

            double current = Math.Max(1.0 / 65536, Math.Sqrt(scaled.X * scaled.X + scaled.Y * scaled.Y));
            double previous = Math.Max(1.0 / 65536,
                Math.Sqrt(PreviousScaled.X * PreviousScaled.X + PreviousScaled.Y * PreviousScaled.Y));
            double gain = Math.Max(0, (current + 0.5 * (current - previous)) / current);
            PreviousScaled = scaled;
            PreviousEventTime = eventTime;
            scaled = new Pair(scaled.X * gain, scaled.Y * gain);
            if (Count > 0 && Math.Abs((float)(Items[Count - 1].Arrival - arrival)) < (float)0.005)
                Items[Count - 1].AllDeltas = true;

            Items[Count++] = new Item
            {
                Arrival = arrival,
                Interval = (float)interval,
                LastTime = arrival,
                ReferenceRaw = raw,
                ReferenceScaled = scaled,
                RemainingRaw = raw,
                RemainingScaled = scaled,
                StateChanged = stateChanged
            };
        }

        internal void Tick(double now, Action<Pair> deliver)
        {
            if (Count == 0)
                return;

            bool transition = false;
            for (int i = 0; i < Count; i++)
                transition |= Items[i].StateChanged;

            if (transition)
            {
                for (int i = 0; i < Count; i++)
                    deliver(Items[i].RemainingScaled.Read());

                Count = 0;
                return;
            }

            // Raw remainders keep an item queued after its scaled movement is spent, as in the recovered queue.
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
            deliver(new Pair(sx, sy).Read());
        }
    }
}

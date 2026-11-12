using System;

namespace AmtPtpControlPanel.WinUI3.Smoothing
{
    /// <summary>
    /// The virtual desktop rectangle in physical pixels.
    /// </summary>
    internal readonly record struct DesktopBounds(int X, int Y, int Width, int Height);

    internal readonly record struct Pixel(int X, int Y);

    /// <summary>
    /// Feeds Windows-processed pointer deltas through the queue and tracks the smoothed cursor position.
    /// </summary>
    internal sealed class ProcessedMotion
    {
        internal readonly ExternalQueue Queue = new ExternalQueue();
        internal Pair Position;
        internal uint? LastEventMs;
        internal double EventSeconds;

        private readonly DesktopBounds _bounds;
        private readonly long _start;
        private readonly double _frequency;
        private readonly double _interval;
        private readonly Action<Pair> _apply;

        internal ProcessedMotion(Pair position, DesktopBounds bounds, long start, double frequency, double interval = 0.01125)
        {
            Position = position;
            _bounds = bounds;
            _start = start;
            _frequency = frequency;
            _interval = interval;
            _apply = Apply;
        }

        internal void Add(long stamp, Pair delta, uint eventMs, bool stateChanged = false)
        {
            double arrival = (stamp - _start) / _frequency;
            if (!LastEventMs.HasValue)
                EventSeconds = arrival;
            else
                EventSeconds += unchecked((int)(eventMs - LastEventMs.Value)) / 1000.0;

            LastEventMs = eventMs;
            Queue.Push(arrival, EventSeconds, _interval, delta, stateChanged, _apply);
        }

        internal void Rebase(Pixel cursor)
        {
            Queue.Clear();
            Position = new Pair(cursor.X, cursor.Y);
            LastEventMs = null;
            EventSeconds = 0;
        }

        internal void CorrectPosition(Pixel expected, Pixel actual)
        {
            SetPosition(Position.X + ((double)actual.X - expected.X), Position.Y + ((double)actual.Y - expected.Y));
        }

        private void Apply(Pair scaled)
        {
            SetPosition(Position.X + scaled.X, Position.Y + scaled.Y);
        }

        private void SetPosition(double x, double y)
        {
            Position = new Pair(
                Math.Clamp(x, _bounds.X, (double)_bounds.X + _bounds.Width - 1),
                Math.Clamp(y, _bounds.Y, (double)_bounds.Y + _bounds.Height - 1));
        }

        internal Pixel Sample(long stamp)
        {
            double now = (stamp - _start) / _frequency;
            Queue.Tick(now, _apply);
            return new Pixel((int)Math.Round(Position.X, MidpointRounding.ToEven), (int)Math.Round(Position.Y, MidpointRounding.ToEven));
        }
    }
}

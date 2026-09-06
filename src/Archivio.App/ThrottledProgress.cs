using System.Diagnostics;

namespace Archivio.App;

internal sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _target;
    private readonly Func<T, bool>? _reportImmediately;
    private readonly long _minimumTimestampInterval;
    private long _lastReportTimestamp = long.MinValue;

    public ThrottledProgress(
        IProgress<T> target,
        TimeSpan minimumInterval,
        Func<T, bool>? reportImmediately = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumInterval),
                minimumInterval,
                "The progress interval cannot be negative.");
        }

        _target = target;
        _reportImmediately = reportImmediately;
        _minimumTimestampInterval = (long)Math.Ceiling(
            minimumInterval.TotalSeconds * Stopwatch.Frequency);
    }

    public void Report(T value)
    {
        var now = Stopwatch.GetTimestamp();
        if (_reportImmediately?.Invoke(value) == true)
        {
            Interlocked.Exchange(ref _lastReportTimestamp, now);
            _target.Report(value);
            return;
        }

        while (true)
        {
            var previous = Volatile.Read(ref _lastReportTimestamp);
            if (previous != long.MinValue && now - previous < _minimumTimestampInterval)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastReportTimestamp, now, previous) == previous)
            {
                _target.Report(value);
                return;
            }
        }
    }
}

namespace Bds.Core.Util;

public sealed class Backoff(TimeSpan min, TimeSpan max)
{
    int _attempt;

    public TimeSpan Next()
    {
        var ms = Math.Min(max.TotalMilliseconds, min.TotalMilliseconds * Math.Pow(2, _attempt++));
        return TimeSpan.FromMilliseconds(ms * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    public void Reset() => _attempt = 0;
}

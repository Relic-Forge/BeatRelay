using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeatRelay.BeatLeader;

public sealed class BeatLeaderRateLimiter
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private DateTimeOffset nextAllowedUtc = DateTimeOffset.MinValue;

    public BeatLeaderRateLimiter()
        : this(TimeSpan.FromSeconds(1), () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    public BeatLeaderRateLimiter(
        TimeSpan minimumInterval,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), "Minimum interval cannot be negative.");
        }

        MinimumInterval = minimumInterval;
        this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        this.delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public TimeSpan MinimumInterval { get; }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = utcNow();
            if (now < nextAllowedUtc)
            {
                await delayAsync(nextAllowedUtc - now, cancellationToken).ConfigureAwait(false);
                now = utcNow();
            }

            nextAllowedUtc = now + MinimumInterval;
        }
        finally
        {
            gate.Release();
        }
    }
}

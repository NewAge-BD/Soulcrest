namespace Soulcrest.App.Services;

/// <summary>
/// Local replay controls driven by caller-supplied monotonic time deltas. This model owns no
/// timer and never changes recorded sessions, source timestamps or statistical totals.
/// </summary>
public sealed class LootTrackerReplayPlayback
{
    private decimal _fractionalTicks;
    public string? SessionId { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsLive { get; private set; }
    public bool FollowingEnd { get; private set; } = true;
    public bool IsPlaying { get; private set; }
    public int Direction { get; private set; } = 1;
    public double Speed { get; private set; } = 1;

    public void UpdateSession(string id, TimeSpan duration, bool isLive)
    {
        ArgumentNullException.ThrowIfNull(id);
        var end = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        if (!string.Equals(SessionId, id, StringComparison.Ordinal))
        {
            SessionId = id; Duration = end; Elapsed = end; IsLive = isLive;
            FollowingEnd = true; IsPlaying = false; Direction = 1; Speed = 1; _fractionalTicks = 0;
            return;
        }
        var ended = IsLive && !isLive;
        var clipped = Elapsed > end;
        var shortenedBoundary = end < Duration && Elapsed >= end;
        Duration = end; IsLive = isLive;
        if (FollowingEnd) Elapsed = end;
        else if (clipped) Elapsed = end;
        if (ended || clipped || shortenedBoundary || end == TimeSpan.Zero) Pause();
    }

    public void Seek(TimeSpan elapsed)
    {
        Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed > Duration ? Duration : elapsed;
        FollowingEnd = false; Pause();
    }

    public void JumpToEnd()
    {
        Elapsed = Duration; FollowingEnd = true; Direction = 1; Pause();
    }

    public void Step(TimeSpan delta)
    {
        // Compare before adding so even TimeSpan.MinValue/MaxValue remain safe controls.
        if (delta.Ticks >= 0 && delta.Ticks >= Duration.Ticks - Elapsed.Ticks) Seek(Duration);
        else if (delta.Ticks < 0 && delta.Ticks <= -Elapsed.Ticks) Seek(TimeSpan.Zero);
        else Seek(TimeSpan.FromTicks(Elapsed.Ticks + delta.Ticks));
    }

    public void Play(int direction = 1)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        Direction = direction; FollowingEnd = false; _fractionalTicks = 0;
        if (Duration == TimeSpan.Zero) { IsPlaying = false; return; }
        if (direction == 1 && Elapsed >= Duration) Elapsed = TimeSpan.Zero;
        else if (direction == -1 && Elapsed <= TimeSpan.Zero) Elapsed = Duration;
        IsPlaying = true;
    }

    public void Pause() { IsPlaying = false; _fractionalTicks = 0; }
    public void TogglePlayPause() { if (IsPlaying) Pause(); else Play(Direction); }

    public void SetSpeed(double speed)
    {
        if (speed is not (.25 or .5 or 1 or 2 or 4 or 8)) throw new ArgumentOutOfRangeException(nameof(speed));
        Speed = speed;
    }

    public void Advance(TimeSpan elapsedReal)
    {
        if (!IsPlaying || elapsedReal <= TimeSpan.Zero) return;
        var available = Direction == 1 ? Duration.Ticks - Elapsed.Ticks : Elapsed.Ticks;
        // Decimal avoids overflow at the supported speeds and carries sub-tick fractions.
        var scaled = elapsedReal.Ticks * (decimal)Speed + _fractionalTicks;
        if (scaled >= available)
        {
            Elapsed = Direction == 1 ? Duration : TimeSpan.Zero;
            FollowingEnd = Direction == 1 && IsLive;
            Pause(); return;
        }
        var ticks = decimal.ToInt64(decimal.Truncate(scaled));
        _fractionalTicks = scaled - ticks;
        Elapsed = TimeSpan.FromTicks(Elapsed.Ticks + ticks * Direction);
    }
}

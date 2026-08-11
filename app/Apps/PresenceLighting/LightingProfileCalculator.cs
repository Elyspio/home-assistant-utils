namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>
/// Configuration for the daily color-temperature and brightness curve.
/// </summary>
public sealed class CircadianScheduleOptions
{
    /// <summary>Gets the time at which the morning transition starts.</summary>
    public TimeOnly WakeTime { get; init; } = new(7, 0);

    /// <summary>Gets the time at which the daytime target is reached.</summary>
    public TimeOnly DayStartTime { get; init; } = new(9, 0);

    /// <summary>Gets the time at which the nighttime target is reached.</summary>
    public TimeOnly NightStartTime { get; init; } = new(23, 0);

    /// <summary>Gets the fallback evening start used when no sunset is available.</summary>
    public TimeOnly FallbackEveningStartTime { get; init; } = new(18, 0);

    /// <summary>Gets the offset applied to sunset to determine the evening transition start.</summary>
    public TimeSpan EveningStartOffset { get; init; } = TimeSpan.FromHours(-2);

    /// <summary>Gets the daytime color temperature in Kelvin.</summary>
    public int DayKelvin { get; init; } = 4000;

    /// <summary>Gets the nighttime color temperature in Kelvin.</summary>
    public int NightKelvin { get; init; } = 2200;

    /// <summary>Gets the daytime brightness percentage.</summary>
    public int DayBrightnessPercent { get; init; } = 100;
}

/// <summary>
/// Desired light attributes at a point in the circadian curve.
/// </summary>
/// <param name="Kelvin">Color temperature in Kelvin.</param>
/// <param name="BrightnessPercent">Brightness percentage.</param>
public sealed record LightingTarget(int Kelvin, int BrightnessPercent);

/// <summary>
/// Calculates light targets from the local time and current day's sunset.
/// </summary>
public sealed class LightingProfileCalculator
{
    private readonly CircadianScheduleOptions _options;

    /// <summary>
    /// Initializes a new calculator.
    /// </summary>
    /// <param name="options">Daily lighting schedule.</param>
    public LightingProfileCalculator(CircadianScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Calculates the current target for a room.
    /// </summary>
    /// <param name="now">Current local time.</param>
    /// <param name="sunset">Sunset for the current local day, or <see langword="null"/>.</param>
    /// <param name="nightBrightnessPercent">Room-specific nighttime brightness.</param>
    /// <returns>The desired color temperature and brightness.</returns>
    public LightingTarget Calculate(
        DateTimeOffset now,
        DateTimeOffset? sunset,
        int nightBrightnessPercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nightBrightnessPercent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nightBrightnessPercent, 100);

        var wake = AtLocalTime(now, _options.WakeTime);
        var dayStart = AtLocalTime(now, _options.DayStartTime);
        var nightStart = AtLocalTime(now, _options.NightStartTime);
        var eveningStart = sunset?.Add(_options.EveningStartOffset)
            ?? AtLocalTime(now, _options.FallbackEveningStartTime);

        eveningStart = eveningStart < dayStart ? dayStart : eveningStart;
        eveningStart = eveningStart > nightStart ? nightStart : eveningStart;

        if (now <= wake || now >= nightStart)
        {
            return new LightingTarget(_options.NightKelvin, nightBrightnessPercent);
        }

        if (now < dayStart)
        {
            return Interpolate(
                now,
                wake,
                dayStart,
                _options.NightKelvin,
                _options.DayKelvin,
                nightBrightnessPercent,
                _options.DayBrightnessPercent);
        }

        if (now <= eveningStart)
        {
            return new LightingTarget(_options.DayKelvin, _options.DayBrightnessPercent);
        }

        return Interpolate(
            now,
            eveningStart,
            nightStart,
            _options.DayKelvin,
            _options.NightKelvin,
            _options.DayBrightnessPercent,
            nightBrightnessPercent);
    }

    private static DateTimeOffset AtLocalTime(DateTimeOffset now, TimeOnly time) =>
        new(now.Year, now.Month, now.Day, time.Hour, time.Minute, time.Second, now.Offset);

    private static LightingTarget Interpolate(
        DateTimeOffset now,
        DateTimeOffset start,
        DateTimeOffset end,
        int startKelvin,
        int endKelvin,
        int startBrightness,
        int endBrightness)
    {
        var progress = (now - start).TotalSeconds / (end - start).TotalSeconds;
        progress = Math.Clamp(progress, 0, 1);

        return new LightingTarget(
            (int)Math.Round(startKelvin + ((endKelvin - startKelvin) * progress)),
            (int)Math.Round(startBrightness + ((endBrightness - startBrightness) * progress)));
    }
}

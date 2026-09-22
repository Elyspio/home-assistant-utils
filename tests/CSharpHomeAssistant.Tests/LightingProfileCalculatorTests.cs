using CSharpHomeAssistant.Apps.PresenceLighting;
using Shouldly;
using Xunit;

namespace CSharpHomeAssistant.Tests;

public sealed class LightingProfileCalculatorTests
{
    private static readonly CircadianScheduleOptions Schedule = new()
    {
        WakeTime = new TimeOnly(7, 0),
        DayStartTime = new TimeOnly(9, 0),
        NightStartTime = new TimeOnly(23, 0),
        FallbackEveningStartTime = new TimeOnly(18, 0),
        EveningStartOffset = TimeSpan.FromHours(-2),
        DayKelvin = 4000,
        NightKelvin = 2200,
        DayBrightnessPercent = 100
    };

    private readonly LightingProfileCalculator _calculator = new(Schedule);

    [Theory]
    [InlineData("2026-08-11T07:00:00+02:00", 2200, 40)]
    [InlineData("2026-08-11T08:00:00+02:00", 3100, 70)]
    [InlineData("2026-08-11T12:00:00+02:00", 4000, 100)]
    [InlineData("2026-08-11T20:45:00+02:00", 3100, 70)]
    [InlineData("2026-08-11T23:00:00+02:00", 2200, 40)]
    public void Calculate_returns_expected_target_across_the_day(
        string currentTime,
        int expectedKelvin,
        int expectedBrightness)
    {
        // Arrange
        var now = DateTimeOffset.Parse(currentTime);
        var sunset = DateTimeOffset.Parse("2026-08-11T20:30:00+02:00");

        // Act
        var target = _calculator.Calculate(now, sunset, 40);

        // Assert
        target.ShouldBe(new LightingTarget(expectedKelvin, expectedBrightness));
    }

    [Fact]
    public void Calculate_uses_fixed_evening_fallback_when_sunset_is_unavailable()
    {
        // Arrange
        var now = DateTimeOffset.Parse("2026-08-11T20:30:00+02:00");

        // Act
        var target = _calculator.Calculate(now, sunset: null, nightBrightnessPercent: 40);

        // Assert
        target.ShouldBe(new LightingTarget(3100, 70));
    }
}

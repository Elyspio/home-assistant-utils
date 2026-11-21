using CSharpHomeAssistant.Apps.PresenceLighting;
using Shouldly;
using Xunit;

namespace CSharpHomeAssistant.Tests;

public sealed class RoomPresenceStateMachineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-11T20:00:00+02:00");

    [Fact]
    public void Occupancy_turns_light_on_and_confirmed_vacancy_turns_it_off_after_delay()
    {
        // Arrange
        var machine = CreateMachine(TimeSpan.FromMinutes(5));
        machine.SynchronizeOccupancy(0, Now);

        // Act
        var entered = machine.UpdateOccupancy(1, Now.AddSeconds(1));
        var left = machine.UpdateOccupancy(0, Now.AddMinutes(1));
        var beforeDeadline = machine.Advance(Now.AddMinutes(5).AddSeconds(59));
        var afterDeadline = machine.Advance(Now.AddMinutes(6));

        // Assert
        entered.Kind.ShouldBe(RoomActionKind.TurnOn);
        left.Kind.ShouldBe(RoomActionKind.None);
        beforeDeadline.Kind.ShouldBe(RoomActionKind.None);
        afterDeadline.Kind.ShouldBe(RoomActionKind.TurnOff);
        machine.Mode.ShouldBe(RoomAutomationMode.Vacant);
    }

    [Fact]
    public void Reentry_cancels_pending_vacancy()
    {
        // Arrange
        var machine = CreateMachine(TimeSpan.FromSeconds(45));
        machine.SynchronizeOccupancy(1, Now);
        machine.UpdateOccupancy(0, Now.AddSeconds(5));

        // Act
        var reentered = machine.UpdateOccupancy(1, Now.AddSeconds(40));
        var afterOldDeadline = machine.Advance(Now.AddMinutes(1));

        // Assert
        reentered.Kind.ShouldBe(RoomActionKind.TurnOn);
        afterOldDeadline.Kind.ShouldBe(RoomActionKind.None);
        machine.Mode.ShouldBe(RoomAutomationMode.Automatic);
    }

    [Fact]
    public void Frigate_outage_never_turns_light_off()
    {
        // Arrange
        var machine = CreateMachine(TimeSpan.FromSeconds(45));
        machine.SynchronizeOccupancy(1, Now);
        machine.UpdateOccupancy(0, Now.AddSeconds(1));

        // Act
        machine.MarkUnavailable();
        var decision = machine.Advance(Now.AddHours(1));

        // Assert
        decision.Kind.ShouldBe(RoomActionKind.None);
        machine.Mode.ShouldBe(RoomAutomationMode.Unavailable);
    }

    [Fact]
    public void Manual_override_resumes_automatic_profile_after_one_hour()
    {
        // Arrange
        var machine = CreateMachine(TimeSpan.FromMinutes(5));
        machine.SynchronizeOccupancy(1, Now);

        // Act
        machine.RegisterManualChange(ManualLightChange.AttributesChanged, Now, isNight: false);
        var beforeExpiry = machine.Advance(Now.AddMinutes(59));
        var atExpiry = machine.Advance(Now.AddHours(1));

        // Assert
        beforeExpiry.Kind.ShouldBe(RoomActionKind.None);
        atExpiry.Kind.ShouldBe(RoomActionKind.ApplyProfile);
        machine.Mode.ShouldBe(RoomAutomationMode.Automatic);
    }

    [Fact]
    public void Bedroom_manual_off_at_night_stays_latched_until_vacancy_is_confirmed()
    {
        // Arrange
        var machine = CreateMachine(TimeSpan.FromMinutes(5), isBedroom: true);
        machine.SynchronizeOccupancy(1, Now);

        // Act
        machine.RegisterManualChange(ManualLightChange.TurnedOff, Now, isNight: true);
        var afterOneHour = machine.Advance(Now.AddHours(1));
        machine.UpdateOccupancy(0, Now.AddHours(2));
        var confirmedVacant = machine.Advance(Now.AddHours(2).AddMinutes(5));
        var nextEntry = machine.UpdateOccupancy(1, Now.AddHours(3));

        // Assert
        afterOneHour.Kind.ShouldBe(RoomActionKind.None);
        confirmedVacant.Kind.ShouldBe(RoomActionKind.None);
        nextEntry.Kind.ShouldBe(RoomActionKind.TurnOn);
        machine.Mode.ShouldBe(RoomAutomationMode.Automatic);
    }

    [Fact]
    public void Retained_override_is_restored_only_while_it_is_still_valid()
    {
        var active = CreateMachine(TimeSpan.FromMinutes(5));
        var expired = CreateMachine(TimeSpan.FromMinutes(5));

        active.RestoreOverride(Now.AddMinutes(5), manualOffLatched: false, Now);
        expired.RestoreOverride(Now.AddMinutes(-1), manualOffLatched: false, Now);

        active.Mode.ShouldBe(RoomAutomationMode.ManualOverride);
        active.OverrideUntil.ShouldBe(Now.AddMinutes(5));
        expired.OverrideUntil.ShouldBeNull();
    }

    [Fact]
    public void Retained_bedroom_latch_clears_after_synchronized_vacancy_delay()
    {
        var machine = CreateMachine(TimeSpan.FromMinutes(5), isBedroom: true);
        machine.RestoreOverride(null, manualOffLatched: true, Now);

        machine.SynchronizeOccupancy(0, Now);
        var decision = machine.Advance(Now.AddMinutes(5));

        decision.Kind.ShouldBe(RoomActionKind.None);
        machine.ManualOffLatched.ShouldBeFalse();
        machine.Mode.ShouldBe(RoomAutomationMode.Vacant);
    }

    [Fact]
    public void Manual_light_turned_on_in_a_vacant_room_turns_off_when_override_expires()
    {
        var machine = CreateMachine(TimeSpan.FromMinutes(5));
        machine.SynchronizeOccupancy(0, Now);

        machine.RegisterManualChange(ManualLightChange.AttributesChanged, Now, isNight: false);
        var decision = machine.Advance(Now.AddHours(1));

        decision.Kind.ShouldBe(RoomActionKind.TurnOff);
        machine.Mode.ShouldBe(RoomAutomationMode.Vacant);
    }

    private static RoomPresenceStateMachine CreateMachine(TimeSpan vacancyDelay, bool isBedroom = false) =>
        new(
            new RoomOptions
            {
                Name = "test_room",
                Camera = "test_camera",
                LightEntityId = "light.test",
                VacancyDelay = vacancyDelay,
                IsBedroom = isBedroom,
                NightBrightnessPercent = 40
            },
            new ManualOverrideOptions { Duration = TimeSpan.FromHours(1) });
}

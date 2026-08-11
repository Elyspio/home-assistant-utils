using System.ComponentModel.DataAnnotations;

namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>
/// Configuration for one Frigate camera-to-light mapping.
/// </summary>
public sealed class RoomOptions
{
    /// <summary>Gets the stable room name used in logs and MQTT discovery identifiers.</summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the Frigate camera name.</summary>
    [Required]
    public string Camera { get; init; } = string.Empty;

    /// <summary>Gets an optional Frigate zone used instead of the whole camera frame.</summary>
    public string? Zone { get; init; }

    /// <summary>Gets the Home Assistant light entity identifier.</summary>
    [Required]
    public string LightEntityId { get; init; } = string.Empty;

    /// <summary>Gets the delay before an empty room is confirmed vacant.</summary>
    public TimeSpan VacancyDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets the room-specific nighttime brightness percentage.</summary>
    [Range(0, 100)]
    public int NightBrightnessPercent { get; init; } = 40;

    /// <summary>Gets a value indicating whether nocturnal manual-off latching applies.</summary>
    public bool IsBedroom { get; init; }

    /// <summary>Gets the MQTT object-count topic for this room.</summary>
    public string OccupancyTopic => $"frigate/{Zone ?? Camera}/person";
}

/// <summary>
/// Configuration for externally initiated light changes.
/// </summary>
public sealed class ManualOverrideOptions
{
    /// <summary>Gets the duration of a standard manual override.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Observable operating modes of a room automation.
/// </summary>
public enum RoomAutomationMode
{
    /// <summary>No synchronized Frigate state is available.</summary>
    Unavailable,

    /// <summary>The room is confirmed empty.</summary>
    Vacant,

    /// <summary>An empty reading is waiting for its grace period.</summary>
    VacancyPending,

    /// <summary>The room is occupied and automatically controlled.</summary>
    Automatic,

    /// <summary>An external light change temporarily owns the room.</summary>
    ManualOverride,

    /// <summary>A bedroom light was manually turned off at night.</summary>
    ManualOffLatched
}

/// <summary>
/// Light changes that can open a manual override.
/// </summary>
public enum ManualLightChange
{
    /// <summary>The light was manually turned off.</summary>
    TurnedOff,

    /// <summary>Brightness or color-temperature attributes changed externally.</summary>
    AttributesChanged
}

/// <summary>
/// Action requested by the room state machine.
/// </summary>
public enum RoomActionKind
{
    /// <summary>No Home Assistant call is required.</summary>
    None,

    /// <summary>Turn on the light with the current profile.</summary>
    TurnOn,

    /// <summary>Turn off the light.</summary>
    TurnOff,

    /// <summary>Apply the current profile to an already controlled light.</summary>
    ApplyProfile
}

/// <summary>
/// Decision emitted by a room state transition.
/// </summary>
/// <param name="Kind">Requested light action.</param>
public readonly record struct RoomDecision(RoomActionKind Kind)
{
    /// <summary>Gets a decision that does not call Home Assistant.</summary>
    public static RoomDecision None => new(RoomActionKind.None);
}

/// <summary>
/// Deterministic presence and override state machine for one room.
/// </summary>
public sealed class RoomPresenceStateMachine
{
    private readonly RoomOptions _room;
    private readonly ManualOverrideOptions _manualOverride;
    private int _occupancyCount;
    private DateTimeOffset? _vacancyDeadline;
    private DateTimeOffset? _overrideUntil;
    private bool _available;
    private bool _presenceEnabled = true;
    private bool _manualOffLatched;

    /// <summary>
    /// Initializes a room state machine.
    /// </summary>
    /// <param name="room">Room mapping and vacancy policy.</param>
    /// <param name="manualOverride">Manual override policy.</param>
    public RoomPresenceStateMachine(RoomOptions room, ManualOverrideOptions manualOverride)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(manualOverride);
        if (room.VacancyDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(room), "Vacancy delay cannot be negative.");
        }

        if (manualOverride.Duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(manualOverride), "Override duration cannot be negative.");
        }

        _room = room;
        _manualOverride = manualOverride;
    }

    /// <summary>Gets the current externally observable automation mode.</summary>
    public RoomAutomationMode Mode { get; private set; } = RoomAutomationMode.Unavailable;

    /// <summary>Gets the latest synchronized person count.</summary>
    public int OccupancyCount => _occupancyCount;

    /// <summary>Gets the current override expiration, when applicable.</summary>
    public DateTimeOffset? OverrideUntil => _overrideUntil;

    /// <summary>Gets whether a bedroom manual-off latch is currently active.</summary>
    public bool ManualOffLatched => _manualOffLatched;

    /// <summary>Restores a retained override after a process restart.</summary>
    public void RestoreOverride(DateTimeOffset? overrideUntil, bool manualOffLatched, DateTimeOffset now)
    {
        _manualOffLatched = manualOffLatched;
        _overrideUntil = overrideUntil > now ? overrideUntil : null;
        if (_manualOffLatched)
        {
            Mode = RoomAutomationMode.ManualOffLatched;
        }
        else if (_overrideUntil is not null)
        {
            Mode = RoomAutomationMode.ManualOverride;
        }
    }

    /// <summary>
    /// Establishes a fresh occupancy baseline after startup or reconnect.
    /// </summary>
    /// <param name="count">Current Frigate person count.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The action required for an occupied room.</returns>
    public RoomDecision SynchronizeOccupancy(int count, DateTimeOffset now)
    {
        ValidateCount(count);
        _available = true;
        _occupancyCount = count;
        _vacancyDeadline = null;

        if (!_presenceEnabled)
        {
            return RoomDecision.None;
        }

        if (_manualOffLatched)
        {
            if (count == 0)
            {
                _vacancyDeadline = now.Add(_room.VacancyDelay);
            }

            Mode = RoomAutomationMode.ManualOffLatched;
            return RoomDecision.None;
        }

        if (_overrideUntil is not null && now < _overrideUntil)
        {
            Mode = RoomAutomationMode.ManualOverride;
            return RoomDecision.None;
        }

        Mode = count > 0 ? RoomAutomationMode.Automatic : RoomAutomationMode.Vacant;
        return count > 0 ? new RoomDecision(RoomActionKind.TurnOn) : RoomDecision.None;
    }

    /// <summary>
    /// Applies a new Frigate person count.
    /// </summary>
    /// <param name="count">New person count.</param>
    /// <param name="now">Current time.</param>
    /// <returns>The requested light action.</returns>
    public RoomDecision UpdateOccupancy(int count, DateTimeOffset now)
    {
        ValidateCount(count);
        var previousCount = _occupancyCount;
        _occupancyCount = count;

        if (!_available || !_presenceEnabled)
        {
            return RoomDecision.None;
        }

        if (count > 0)
        {
            _vacancyDeadline = null;
            if (_manualOffLatched)
            {
                Mode = RoomAutomationMode.ManualOffLatched;
                return RoomDecision.None;
            }

            if (_overrideUntil is not null && now < _overrideUntil)
            {
                Mode = RoomAutomationMode.ManualOverride;
                return RoomDecision.None;
            }

            Mode = RoomAutomationMode.Automatic;
            return previousCount == 0
                ? new RoomDecision(RoomActionKind.TurnOn)
                : RoomDecision.None;
        }

        _vacancyDeadline ??= now.Add(_room.VacancyDelay);
        if (_manualOffLatched)
        {
            Mode = RoomAutomationMode.ManualOffLatched;
        }
        else if (_overrideUntil is not null && now < _overrideUntil)
        {
            Mode = RoomAutomationMode.ManualOverride;
        }
        else
        {
            Mode = RoomAutomationMode.VacancyPending;
        }

        return RoomDecision.None;
    }

    /// <summary>
    /// Registers a light change that did not originate from this controller.
    /// </summary>
    /// <param name="change">Kind of external change.</param>
    /// <param name="now">Current time.</param>
    /// <param name="isNight">Whether the current schedule is in its nighttime interval.</param>
    public void RegisterManualChange(ManualLightChange change, DateTimeOffset now, bool isNight)
    {
        if (_room.IsBedroom && isNight && change == ManualLightChange.TurnedOff)
        {
            _manualOffLatched = true;
            _overrideUntil = null;
            if (_occupancyCount == 0)
            {
                _vacancyDeadline = now.Add(_room.VacancyDelay);
            }

            Mode = RoomAutomationMode.ManualOffLatched;
            return;
        }

        _manualOffLatched = false;
        _overrideUntil = now.Add(_manualOverride.Duration);
        if (_occupancyCount == 0)
        {
            _vacancyDeadline = _overrideUntil;
        }

        Mode = RoomAutomationMode.ManualOverride;
    }

    /// <summary>
    /// Advances timers and returns any action whose deadline has elapsed.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <returns>The requested light action.</returns>
    public RoomDecision Advance(DateTimeOffset now)
    {
        if (!_available || !_presenceEnabled)
        {
            return RoomDecision.None;
        }

        if (_manualOffLatched)
        {
            if (_occupancyCount == 0 && _vacancyDeadline is not null && now >= _vacancyDeadline)
            {
                _manualOffLatched = false;
                _vacancyDeadline = null;
                Mode = RoomAutomationMode.Vacant;
            }

            return RoomDecision.None;
        }

        if (_overrideUntil is not null)
        {
            if (now < _overrideUntil)
            {
                return RoomDecision.None;
            }

            _overrideUntil = null;
            if (_occupancyCount > 0)
            {
                Mode = RoomAutomationMode.Automatic;
                return new RoomDecision(RoomActionKind.ApplyProfile);
            }
        }

        if (_occupancyCount == 0 && _vacancyDeadline is not null && now >= _vacancyDeadline)
        {
            _vacancyDeadline = null;
            Mode = RoomAutomationMode.Vacant;
            return new RoomDecision(RoomActionKind.TurnOff);
        }

        return RoomDecision.None;
    }

    /// <summary>
    /// Suspends all vacancy decisions until a fresh synchronization is received.
    /// </summary>
    public void MarkUnavailable()
    {
        _available = false;
        _vacancyDeadline = null;
        Mode = RoomAutomationMode.Unavailable;
    }

    /// <summary>
    /// Enables or disables presence-driven actions without affecting circadian updates.
    /// </summary>
    /// <param name="enabled">Whether presence actions are enabled.</param>
    public void SetPresenceEnabled(bool enabled)
    {
        _presenceEnabled = enabled;
        if (!enabled)
        {
            _vacancyDeadline = null;
        }
    }

    private static void ValidateCount(int count) =>
        ArgumentOutOfRangeException.ThrowIfNegative(count);
}

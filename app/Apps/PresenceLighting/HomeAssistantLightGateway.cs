using CSharpHomeAssistant.Generated;
using NetDaemon.HassModel;

namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>External Home Assistant light change observed by the controller.</summary>
public sealed record ExternalLightChange(string EntityId, ManualLightChange Change);

/// <summary>Home Assistant boundary for light commands and state observation.</summary>
public interface ILightGateway : IDisposable
{
    event Action<ExternalLightChange>? ExternalChange;

    bool IsOn(string entityId);

    void TurnOn(string entityId, LightingTarget target, TimeSpan transition);

    void TurnOff(string entityId, TimeSpan transition);
}

/// <summary>Calls Home Assistant light services while correlating resulting state events.</summary>
public sealed class HomeAssistantLightGateway : ILightGateway
{
    private static readonly TimeSpan CommandObservationMargin = TimeSpan.FromSeconds(8);
    private readonly IHaContext _haContext;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _sync = new();

    public HomeAssistantLightGateway(
        IHaContext haContext,
        TimeProvider timeProvider,
        IEnumerable<RoomOptions> rooms)
    {
        _haContext = haContext;
        _timeProvider = timeProvider;

        foreach (var entityId in rooms.Select(room => room.LightEntityId).Distinct(StringComparer.Ordinal))
        {
            var light = new LightEntity(_haContext, entityId);
            _subscriptions.Add(light.StateAllChanges().Subscribe(change =>
                ObserveChange(entityId, change.Old?.State, change.New?.State,
                    change.Old?.Attributes, change.New?.Attributes)));
        }
    }

    public event Action<ExternalLightChange>? ExternalChange;

    public bool IsOn(string entityId) =>
        string.Equals(new LightEntity(_haContext, entityId).State, "on", StringComparison.Ordinal);

    public void TurnOn(string entityId, LightingTarget target, TimeSpan transition)
    {
        Track(entityId, expectedOn: true, transition);
        new LightEntity(_haContext, entityId).TurnOn(
            transition: (long)Math.Ceiling(transition.TotalSeconds),
            colorTempKelvin: target.Kelvin,
            brightnessPct: target.BrightnessPercent);
    }

    public void TurnOff(string entityId, TimeSpan transition)
    {
        Track(entityId, expectedOn: false, transition);
        new LightEntity(_haContext, entityId).TurnOff(
            transition: (long)Math.Ceiling(transition.TotalSeconds));
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void Track(string entityId, bool expectedOn, TimeSpan transition)
    {
        lock (_sync)
        {
            _pending[entityId] = new PendingCommand(
                expectedOn,
                _timeProvider.GetUtcNow().Add(transition).Add(CommandObservationMargin));
        }
    }

    private void ObserveChange(
        string entityId,
        string? oldState,
        string? newState,
        LightAttributes? oldAttributes,
        LightAttributes? newAttributes)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_sync)
        {
            if (_pending.TryGetValue(entityId, out var pending))
            {
                if (now <= pending.ExpiresAt &&
                    string.Equals(newState, pending.ExpectedOn ? "on" : "off", StringComparison.Ordinal))
                {
                    return;
                }

                if (now <= pending.ExpiresAt && pending.ExpectedOn &&
                    string.Equals(newState, "on", StringComparison.Ordinal))
                {
                    return;
                }

                _pending.Remove(entityId);
            }
        }

        if (string.Equals(newState, "off", StringComparison.Ordinal) &&
            !string.Equals(oldState, "off", StringComparison.Ordinal))
        {
            ExternalChange?.Invoke(new ExternalLightChange(entityId, ManualLightChange.TurnedOff));
            return;
        }

        if (!string.Equals(newState, "on", StringComparison.Ordinal))
        {
            return;
        }

        var manuallyTurnedOn = !string.Equals(oldState, "on", StringComparison.Ordinal);
        var brightnessChanged = Difference(oldAttributes?.Brightness, newAttributes?.Brightness) >= 3;
        var temperatureChanged = Difference(oldAttributes?.ColorTempKelvin, newAttributes?.ColorTempKelvin) >= 50;
        if (manuallyTurnedOn || brightnessChanged || temperatureChanged)
        {
            ExternalChange?.Invoke(new ExternalLightChange(entityId, ManualLightChange.AttributesChanged));
        }
    }

    private static double Difference(double? left, double? right) =>
        left is null || right is null ? 0 : Math.Abs(left.Value - right.Value);

    private sealed record PendingCommand(bool ExpectedOn, DateTimeOffset ExpiresAt);
}

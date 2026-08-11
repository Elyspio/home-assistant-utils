using System.Text;
using System.Text.Json;
using CSharpHomeAssistant.Generated;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using NetDaemon.Client;
using NetDaemon.AppModel;
using NetDaemon.HassModel;

namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>
/// Orchestrates Frigate occupancy, Home Assistant lights, retained controls, and MQTT Discovery.
/// </summary>
[NetDaemonApp]
public sealed class PresenceLightingApp : IAsyncInitializable, IAsyncDisposable
{
    private readonly PresenceLightingOptions _options;
    private readonly MqttOptions _mqttOptions;
    private readonly AutomationHealthState _health;
    private readonly ILogger<PresenceLightingApp> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SunEntities _sunEntities;
    private readonly TimeZoneInfo _timeZone;
    private readonly LightingProfileCalculator _profileCalculator;
    private readonly IHomeAssistantConnection _homeAssistantConnection;
    private readonly IMqttClient _mqtt;
    private readonly ILightGateway _lights;
    private readonly Dictionary<string, RoomRuntime> _rooms;
    private readonly HashSet<string> _synchronizedRooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _snapshotStates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private CancellationTokenSource? _shutdown;
    private Task? _loop;
    private Task? _reconnectLoop;
    private Task? _homeAssistantMonitor;
    private bool _masterEnabled = true;
    private bool _vacationEnabled;
    private DateTimeOffset _nextProfileRefresh = DateTimeOffset.MinValue;

    public PresenceLightingApp(
        IHaContext haContext,
        IHomeAssistantConnection homeAssistantConnection,
        SunEntities sunEntities,
        IOptions<PresenceLightingOptions> options,
        IOptions<MqttOptions> mqttOptions,
        AutomationHealthState health,
        TimeProvider timeProvider,
        ILogger<PresenceLightingApp> logger)
    {
        _options = options.Value;
        _mqttOptions = mqttOptions.Value;
        _health = health;
        _logger = logger;
        _timeProvider = timeProvider;
        _sunEntities = sunEntities;
        _homeAssistantConnection = homeAssistantConnection;

        ValidateConfiguration();
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId);
        _profileCalculator = new LightingProfileCalculator(_options.Schedule);
        _rooms = _options.Rooms.ToDictionary(
            room => room.Name,
            room => new RoomRuntime(room, new RoomPresenceStateMachine(room, _options.ManualOverride)),
            StringComparer.Ordinal);

        _lights = new HomeAssistantLightGateway(haContext, timeProvider, _options.Rooms);
        _lights.ExternalChange += OnExternalLightChange;

        _mqtt = new MqttFactory().CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqtt.DisconnectedAsync += OnDisconnectedAsync;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _health.HomeAssistantConnected = true;
        _homeAssistantMonitor = MonitorHomeAssistantAsync(_shutdown.Token);
        _loop = RunLoopAsync(_shutdown.Token);

        try
        {
            await EnsureConnectedAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Initial MQTT connection failed; reconnecting in background.");
            _reconnectLoop ??= ReconnectLoopAsync(_shutdown.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown is null)
        {
            return;
        }

        _shutdown.Cancel();
        _lights.ExternalChange -= OnExternalLightChange;
        _lights.Dispose();
        _mqtt.ApplicationMessageReceivedAsync -= OnMessageReceivedAsync;
        _mqtt.DisconnectedAsync -= OnDisconnectedAsync;

        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }

            if (_homeAssistantMonitor is not null)
            {
                await _homeAssistantMonitor.ConfigureAwait(false);
            }

            if (_mqtt.IsConnected)
            {
                await PublishAsync($"{_options.TopicRoot}/availability", "offline", true, CancellationToken.None)
                    .ConfigureAwait(false);
                await _mqtt.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _mqtt.Dispose();
            _gate.Dispose();
            _connectGate.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mqtt.IsConnected)
            {
                return;
            }

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_mqttOptions.Host, _mqttOptions.Port)
                .WithClientId(_mqttOptions.ClientId)
                .WithWillTopic($"{_options.TopicRoot}/availability")
                .WithWillPayload("offline")
                .WithWillRetain()
                .Build();

            if (!string.IsNullOrWhiteSpace(_mqttOptions.User))
            {
                options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_mqttOptions.Host, _mqttOptions.Port)
                    .WithClientId(_mqttOptions.ClientId)
                    .WithCredentials(_mqttOptions.User, _mqttOptions.Password)
                    .WithWillTopic($"{_options.TopicRoot}/availability")
                    .WithWillPayload("offline")
                    .WithWillRetain()
                    .Build();
            }

            await _mqtt.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            await SubscribeAsync(cancellationToken).ConfigureAwait(false);
            await PublishDiscoveryAndStateAsync(cancellationToken).ConfigureAwait(false);
            await PublishAsync($"{_mqttOptions.FrigateTopicPrefix}/onConnect", string.Empty, false, cancellationToken)
                .ConfigureAwait(false);

            _health.MqttConnected = true;
            _health.OccupancySynchronized = false;
            _logger.LogInformation("Connected to MQTT at {Host}:{Port}; waiting for all room counts.",
                _mqttOptions.Host, _mqttOptions.Port);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        var retainedControllerTopics = new[]
        {
            $"{_options.TopicRoot}/+/set",
            $"{_options.TopicRoot}/rooms/+/presence/set",
            $"{_options.TopicRoot}/rooms/+/runtime"
        };

        foreach (var topic in retainedControllerTopics)
        {
            await _mqtt.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken)
                .ConfigureAwait(false);
        }

        // Let retained overrides and switches arrive before retained Frigate counts can trigger commands.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        var frigateTopics = new[]
        {
            $"{_mqttOptions.FrigateTopicPrefix}/available",
            $"{_mqttOptions.FrigateTopicPrefix}/camera_activity",
            $"{_mqttOptions.FrigateTopicPrefix}/+/person",
            $"{_mqttOptions.FrigateTopicPrefix}/+/snapshots/state"
        };

        foreach (var topic in frigateTopics)
        {
            await _mqtt.SubscribeAsync(topic, MqttQualityOfServiceLevel.AtLeastOnce, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PublishDiscoveryAndStateAsync(CancellationToken cancellationToken)
    {
        foreach (var publication in MqttDiscovery.Build(_options))
        {
            await PublishAsync(publication.Topic, publication.Payload, publication.Retain, cancellationToken)
                .ConfigureAwait(false);
        }

        await PublishAsync($"{_options.TopicRoot}/availability", "online", true, cancellationToken).ConfigureAwait(false);
        await PublishAsync($"{_options.TopicRoot}/master/state", _masterEnabled ? "ON" : "OFF", true, cancellationToken)
            .ConfigureAwait(false);
        foreach (var room in _rooms.Values)
        {
            await PublishAsync(RoomTopic(room.Options, "presence/state"), room.PresenceEnabled ? "ON" : "OFF", true, cancellationToken)
                .ConfigureAwait(false);
            await PublishDiagnosticsAsync(room, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs eventArgs)
    {
        if (_shutdown is null)
        {
            return;
        }

        var topic = eventArgs.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(eventArgs.ApplicationMessage.PayloadSegment).Trim();
        await _gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            await HandleMessageAsync(topic, payload, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to process MQTT message on {Topic}.", topic);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleMessageAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        if (string.Equals(topic, $"{_mqttOptions.FrigateTopicPrefix}/available", StringComparison.Ordinal))
        {
            if (string.Equals(payload, "online", StringComparison.OrdinalIgnoreCase))
            {
                _synchronizedRooms.Clear();
                _health.OccupancySynchronized = false;
                await PublishAsync($"{_mqttOptions.FrigateTopicPrefix}/onConnect", string.Empty, false, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                MarkFrigateUnavailable();
                foreach (var unavailableRoom in _rooms.Values)
                {
                    await PublishDiagnosticsAsync(unavailableRoom, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        if (string.Equals(topic, $"{_mqttOptions.FrigateTopicPrefix}/camera_activity", StringComparison.Ordinal))
        {
            if (FrigateOccupancyParser.TryParseCameraActivity(payload, _options.Rooms, out var updates))
            {
                foreach (var update in updates)
                {
                    await ApplyOccupancyAsync(update, cancellationToken).ConfigureAwait(false);
                }
            }

            return;
        }

        if (FrigateOccupancyParser.TryParseCount(
                topic,
                payload,
                _options.Rooms,
                out var countUpdate,
                _mqttOptions.FrigateTopicPrefix))
        {
            await ApplyOccupancyAsync(countUpdate, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (topic.StartsWith($"{_mqttOptions.FrigateTopicPrefix}/", StringComparison.Ordinal) &&
            topic.EndsWith("/snapshots/state", StringComparison.Ordinal))
        {
            await HandleSnapshotStateAsync(topic, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(topic, $"{_options.TopicRoot}/master/set", StringComparison.Ordinal))
        {
            _masterEnabled = IsOn(payload);
            await PublishAsync($"{_options.TopicRoot}/master/state", _masterEnabled ? "ON" : "OFF", true, cancellationToken)
                .ConfigureAwait(false);
            if (_masterEnabled)
            {
                await ReconcileRoomsAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (string.Equals(topic, $"{_options.TopicRoot}/vacation/set", StringComparison.Ordinal))
        {
            _vacationEnabled = IsOn(payload);
            foreach (var camera in _options.Rooms.Select(room => room.Camera).Distinct(StringComparer.Ordinal))
            {
                await PublishAsync($"{_mqttOptions.FrigateTopicPrefix}/{camera}/snapshots/set",
                    _vacationEnabled ? "ON" : "OFF", false, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var room = _rooms.Values.FirstOrDefault(candidate =>
            string.Equals(topic, RoomTopic(candidate.Options, "presence/set"), StringComparison.Ordinal));
        if (room is not null)
        {
            room.PresenceEnabled = IsOn(payload);
            room.StateMachine.SetPresenceEnabled(room.PresenceEnabled);
            await PublishAsync(RoomTopic(room.Options, "presence/state"), room.PresenceEnabled ? "ON" : "OFF", true, cancellationToken)
                .ConfigureAwait(false);
            if (room.PresenceEnabled && _synchronizedRooms.Contains(room.Options.Name))
            {
                await ExecuteAsync(room, room.StateMachine.SynchronizeOccupancy(room.StateMachine.OccupancyCount, LocalNow()),
                    _options.OccupancyTransition, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        room = _rooms.Values.FirstOrDefault(candidate =>
            string.Equals(topic, RoomTopic(candidate.Options, "runtime"), StringComparison.Ordinal));
        if (room is not null && TryParseRuntime(payload, out var retained))
        {
            room.StateMachine.RestoreOverride(retained.OverrideUntil, retained.ManualOffLatched, LocalNow());
            await PublishDiagnosticsAsync(room, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyOccupancyAsync(RoomOccupancyUpdate update, CancellationToken cancellationToken)
    {
        if (!_rooms.TryGetValue(update.RoomName, out var room))
        {
            return;
        }

        var firstReading = _synchronizedRooms.Add(room.Options.Name);
        var decision = firstReading
            ? room.StateMachine.SynchronizeOccupancy(update.Count, LocalNow())
            : room.StateMachine.UpdateOccupancy(update.Count, LocalNow());

        _health.OccupancySynchronized = _synchronizedRooms.Count == _rooms.Count;
        await ExecuteAsync(room, decision, _options.OccupancyTransition, cancellationToken).ConfigureAwait(false);
        await PublishDiagnosticsAsync(room, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = LocalNow();
                foreach (var room in _rooms.Values)
                {
                    var previousOverride = room.StateMachine.OverrideUntil;
                    var previousLatch = room.StateMachine.ManualOffLatched;
                    var decision = room.StateMachine.Advance(now);
                    await ExecuteAsync(room, decision,
                        decision.Kind == RoomActionKind.TurnOff ? _options.VacancyTransition : _options.OccupancyTransition,
                        cancellationToken).ConfigureAwait(false);
                    if (previousOverride != room.StateMachine.OverrideUntil ||
                        previousLatch != room.StateMachine.ManualOffLatched)
                    {
                        await PersistRuntimeAsync(room, cancellationToken).ConfigureAwait(false);
                        await PublishDiagnosticsAsync(room, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (now >= _nextProfileRefresh)
                {
                    _nextProfileRefresh = now.Add(_options.ProfileRefreshInterval);
                    await RefreshProfilesAsync(now, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task RefreshProfilesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_masterEnabled)
        {
            return;
        }

        foreach (var room in _rooms.Values)
        {
            if (_lights.IsOn(room.Options.LightEntityId) &&
                room.StateMachine.Mode is not RoomAutomationMode.ManualOverride and not RoomAutomationMode.ManualOffLatched)
            {
                await ExecuteAsync(room, new RoomDecision(RoomActionKind.ApplyProfile), _options.ProfileTransition, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteAsync(
        RoomRuntime room,
        RoomDecision decision,
        TimeSpan transition,
        CancellationToken cancellationToken)
    {
        if (!_masterEnabled || decision.Kind == RoomActionKind.None)
        {
            return;
        }

        if (decision.Kind == RoomActionKind.TurnOff)
        {
            _lights.TurnOff(room.Options.LightEntityId, transition);
            _logger.LogInformation("{Room}: light off after confirmed vacancy.", room.Options.Name);
        }
        else
        {
            room.Target = CalculateTarget(room.Options);
            _lights.TurnOn(room.Options.LightEntityId, room.Target, transition);
            _logger.LogInformation("{Room}: {Action}, {Kelvin} K at {Brightness}%.",
                room.Options.Name, decision.Kind, room.Target.Kelvin, room.Target.BrightnessPercent);
        }

        await PublishDiagnosticsAsync(room, cancellationToken).ConfigureAwait(false);
    }

    private void OnExternalLightChange(ExternalLightChange change)
    {
        if (_shutdown is null || _shutdown.IsCancellationRequested)
        {
            return;
        }

        _ = HandleExternalLightChangeAsync(change, _shutdown.Token);
    }

    private async Task HandleExternalLightChangeAsync(ExternalLightChange change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = LocalNow();
            foreach (var room in _rooms.Values.Where(candidate =>
                         string.Equals(candidate.Options.LightEntityId, change.EntityId, StringComparison.Ordinal)))
            {
                room.StateMachine.RegisterManualChange(change.Change, now, IsNight(now));
                await PersistRuntimeAsync(room, cancellationToken).ConfigureAwait(false);
                await PublishDiagnosticsAsync(room, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Room}: external light change opened mode {Mode}.",
                    room.Options.Name, room.StateMachine.Mode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReconcileRoomsAsync(CancellationToken cancellationToken)
    {
        foreach (var room in _rooms.Values)
        {
            if (room.PresenceEnabled && room.StateMachine.OccupancyCount > 0 &&
                room.StateMachine.Mode is not RoomAutomationMode.ManualOverride and not RoomAutomationMode.ManualOffLatched)
            {
                await ExecuteAsync(room, new RoomDecision(RoomActionKind.ApplyProfile), _options.OccupancyTransition, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (room.PresenceEnabled && room.StateMachine.Mode == RoomAutomationMode.Vacant &&
                     _lights.IsOn(room.Options.LightEntityId))
            {
                await ExecuteAsync(room, new RoomDecision(RoomActionKind.TurnOff), _options.VacancyTransition, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task HandleSnapshotStateAsync(string topic, string payload, CancellationToken cancellationToken)
    {
        var prefixLength = _mqttOptions.FrigateTopicPrefix.Length + 1;
        var camera = topic[prefixLength..^"/snapshots/state".Length];
        _snapshotStates[camera] = IsOn(payload);

        var cameras = _options.Rooms.Select(room => room.Camera).Distinct(StringComparer.Ordinal).ToArray();
        if (cameras.All(candidate => _snapshotStates.TryGetValue(candidate, out var state) && state == _vacationEnabled))
        {
            await PublishAsync($"{_options.TopicRoot}/vacation/state", _vacationEnabled ? "ON" : "OFF", true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void MarkFrigateUnavailable()
    {
        _synchronizedRooms.Clear();
        _health.OccupancySynchronized = false;
        foreach (var room in _rooms.Values)
        {
            room.StateMachine.MarkUnavailable();
        }
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs eventArgs)
    {
        _health.MqttConnected = false;
        MarkFrigateUnavailable();
        if (_shutdown is not null && !_shutdown.IsCancellationRequested)
        {
            _reconnectLoop ??= ReconnectLoopAsync(_shutdown.Token);
        }

        return Task.CompletedTask;
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_mqtt.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "MQTT reconnect attempt failed.");
                }
            }
        }
        finally
        {
            _reconnectLoop = null;
        }
    }

    private async Task MonitorHomeAssistantAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _homeAssistantConnection.WaitForConnectionToCloseAsync(cancellationToken).ConfigureAwait(false);
            _health.HomeAssistantConnected = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PublishDiagnosticsAsync(RoomRuntime room, CancellationToken cancellationToken)
    {
        await PublishAsync(RoomTopic(room.Options, "occupancy"), room.StateMachine.OccupancyCount.ToString(), true, cancellationToken)
            .ConfigureAwait(false);
        await PublishAsync(RoomTopic(room.Options, "mode"), room.StateMachine.Mode.ToString(), true, cancellationToken)
            .ConfigureAwait(false);
        room.Target ??= CalculateTarget(room.Options);
        await PublishAsync(RoomTopic(room.Options, "target"),
            $"{room.Target.Kelvin} K / {room.Target.BrightnessPercent}%", true, cancellationToken).ConfigureAwait(false);
    }

    private Task PersistRuntimeAsync(RoomRuntime room, CancellationToken cancellationToken) =>
        PublishAsync(RoomTopic(room.Options, "runtime"), JsonSerializer.Serialize(new RetainedRuntime(
            room.StateMachine.OverrideUntil, room.StateMachine.ManualOffLatched)), true, cancellationToken);

    private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken)
    {
        if (!_mqtt.IsConnected)
        {
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _mqtt.PublishAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private LightingTarget CalculateTarget(RoomOptions room) =>
        _profileCalculator.Calculate(LocalNow(), GetSunset(), room.NightBrightnessPercent);

    private DateTimeOffset? GetSunset()
    {
        if (!DateTimeOffset.TryParse(_sunEntities.Sun.Attributes?.NextSetting, out var nextSetting))
        {
            return null;
        }

        var localNow = LocalNow();
        var setting = TimeZoneInfo.ConvertTime(nextSetting, _timeZone);
        return setting.Date > localNow.Date ? setting.AddDays(-1) : setting;
    }

    private DateTimeOffset LocalNow() => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _timeZone);

    private bool IsNight(DateTimeOffset now) =>
        TimeOnly.FromDateTime(now.DateTime) >= _options.Schedule.NightStartTime ||
        TimeOnly.FromDateTime(now.DateTime) < _options.Schedule.WakeTime;

    private static bool IsOn(string payload) => string.Equals(payload, "ON", StringComparison.OrdinalIgnoreCase);

    private string RoomTopic(RoomOptions room, string suffix) => $"{_options.TopicRoot}/rooms/{room.Name}/{suffix}";

    private static bool TryParseRuntime(string payload, out RetainedRuntime runtime)
    {
        try
        {
            runtime = JsonSerializer.Deserialize<RetainedRuntime>(payload) ?? new RetainedRuntime(null, false);
            return true;
        }
        catch (JsonException)
        {
            runtime = new RetainedRuntime(null, false);
            return false;
        }
    }

    private void ValidateConfiguration()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_mqttOptions.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.TopicRoot);
        if (_options.Rooms.Count == 0 || _options.Rooms.Select(room => room.Name).Distinct(StringComparer.Ordinal).Count() != _options.Rooms.Count)
        {
            throw new InvalidOperationException("PresenceLighting rooms must be non-empty and have unique names.");
        }

        if (_options.TickInterval <= TimeSpan.Zero || _options.ProfileRefreshInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Lighting timer intervals must be positive.");
        }

        foreach (var room in _options.Rooms)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(room.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(room.Camera);
            ArgumentException.ThrowIfNullOrWhiteSpace(room.LightEntityId);
            if (room.NightBrightnessPercent is < 0 or > 100)
            {
                throw new InvalidOperationException($"Night brightness for {room.Name} must be between 0 and 100.");
            }
        }
    }

    private sealed class RoomRuntime(RoomOptions options, RoomPresenceStateMachine stateMachine)
    {
        public RoomOptions Options { get; } = options;
        public RoomPresenceStateMachine StateMachine { get; } = stateMachine;
        public bool PresenceEnabled { get; set; } = true;
        public LightingTarget? Target { get; set; }
    }

    private sealed record RetainedRuntime(DateTimeOffset? OverrideUntil, bool ManualOffLatched);
}

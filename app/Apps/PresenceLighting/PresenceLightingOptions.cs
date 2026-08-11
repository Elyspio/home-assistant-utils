using System.ComponentModel.DataAnnotations;

namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>MQTT connection used by Frigate, Home Assistant discovery, and retained state.</summary>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    [Required]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 1883;

    public string? User { get; init; }

    public string? Password { get; init; }

    [Required]
    public string FrigateTopicPrefix { get; init; } = "frigate";

    [Required]
    public string ClientId { get; init; } = "home-assistant-utils";
}

/// <summary>Complete configuration of presence-driven circadian lighting.</summary>
public sealed class PresenceLightingOptions
{
    public const string SectionName = "PresenceLighting";

    [Required]
    public string TopicRoot { get; init; } = "home-assistant-utils/presence-lighting";

    [Required]
    public string TimeZoneId { get; init; } = "Europe/Paris";

    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ProfileRefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan OccupancyTransition { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan VacancyTransition { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ProfileTransition { get; init; } = TimeSpan.FromSeconds(60);

    public CircadianScheduleOptions Schedule { get; init; } = new();

    public ManualOverrideOptions ManualOverride { get; init; } = new();

    [MinLength(1)]
    public List<RoomOptions> Rooms { get; init; } = [];
}

/// <summary>Mutable dependency state exposed through Kubernetes health probes.</summary>
public sealed class AutomationHealthState
{
    private int _mqttConnected;
    private int _occupancySynchronized;
    private int _homeAssistantConnected;

    public bool HomeAssistantConnected
    {
        get => Volatile.Read(ref _homeAssistantConnected) == 1;
        set => Volatile.Write(ref _homeAssistantConnected, value ? 1 : 0);
    }

    public bool MqttConnected
    {
        get => Volatile.Read(ref _mqttConnected) == 1;
        set => Volatile.Write(ref _mqttConnected, value ? 1 : 0);
    }

    public bool OccupancySynchronized
    {
        get => Volatile.Read(ref _occupancySynchronized) == 1;
        set => Volatile.Write(ref _occupancySynchronized, value ? 1 : 0);
    }

    public bool Ready => HomeAssistantConnected && MqttConnected && OccupancySynchronized;
}

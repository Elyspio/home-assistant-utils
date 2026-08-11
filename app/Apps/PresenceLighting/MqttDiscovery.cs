using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>A retained MQTT publication.</summary>
public sealed record MqttPublication(string Topic, string Payload, bool Retain = true);

/// <summary>Builds Home Assistant MQTT Discovery entities for the controller.</summary>
public static class MqttDiscovery
{
    private const string DiscoveryRoot = "homeassistant";

    public static IReadOnlyList<MqttPublication> Build(PresenceLightingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var publications = new List<MqttPublication>();

        publications.Add(Switch(options, "master", "Éclairage automatique", $"{options.TopicRoot}/master"));
        publications.Add(Switch(options, "vacation", "Mode vacances (captures)", $"{options.TopicRoot}/vacation"));
        publications.Add(Entity(
            "binary_sensor",
            "availability",
            "Disponibilité éclairage automatique",
            $"{options.TopicRoot}/availability",
            new Dictionary<string, object?>
            {
                ["payload_on"] = "online",
                ["payload_off"] = "offline",
                ["device_class"] = "connectivity"
            }));

        foreach (var room in options.Rooms)
        {
            var roomRoot = $"{options.TopicRoot}/rooms/{room.Name}";
            publications.Add(Switch(options, $"{room.Name}_presence", $"Présence {room.Name}", $"{roomRoot}/presence"));
            publications.Add(Entity("sensor", $"{room.Name}_occupancy", $"Occupation {room.Name}", $"{roomRoot}/occupancy", new()
            {
                ["unit_of_measurement"] = "personnes",
                ["icon"] = "mdi:account-multiple"
            }));
            publications.Add(Entity("sensor", $"{room.Name}_mode", $"Mode {room.Name}", $"{roomRoot}/mode", new()
            {
                ["icon"] = "mdi:state-machine"
            }));
            publications.Add(Entity("sensor", $"{room.Name}_target", $"Cible lumière {room.Name}", $"{roomRoot}/target", new()
            {
                ["icon"] = "mdi:lightbulb-auto"
            }));
        }

        return publications;
    }

    private static MqttPublication Switch(
        PresenceLightingOptions options,
        string objectId,
        string name,
        string topicRoot) =>
        Entity("switch", objectId, name, $"{topicRoot}/state", new()
        {
            ["command_topic"] = $"{topicRoot}/set",
            ["payload_on"] = "ON",
            ["payload_off"] = "OFF",
            ["retain"] = true,
            ["availability_topic"] = $"{options.TopicRoot}/availability"
        });

    private static MqttPublication Entity(
        string component,
        string objectId,
        string name,
        string stateTopic,
        Dictionary<string, object?> additional)
    {
        var payload = new Dictionary<string, object?>(additional)
        {
            ["name"] = name,
            ["unique_id"] = $"home_assistant_utils_{objectId}",
            ["state_topic"] = stateTopic,
            ["device"] = new
            {
                identifiers = new[] { "home_assistant_utils_presence_lighting" },
                name = "Éclairage adaptatif Frigate",
                manufacturer = "home-assistant-utils",
                model = "NetDaemon Presence Lighting"
            }
        };

        return new MqttPublication(
            $"{DiscoveryRoot}/{component}/home_assistant_utils/{objectId}/config",
            JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

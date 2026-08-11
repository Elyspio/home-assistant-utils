using System.Globalization;
using System.Text.Json;

namespace CSharpHomeAssistant.Apps.PresenceLighting;

/// <summary>
/// A normalized room occupancy count received from Frigate.
/// </summary>
/// <param name="RoomName">Configured room name.</param>
/// <param name="Count">Number of tracked people.</param>
public sealed record RoomOccupancyUpdate(string RoomName, int Count);

/// <summary>
/// Parses Frigate count topics and full camera activity snapshots.
/// </summary>
public static class FrigateOccupancyParser
{
    /// <summary>
    /// Parses a direct Frigate person-count topic.
    /// </summary>
    /// <param name="topic">MQTT topic.</param>
    /// <param name="payload">MQTT payload.</param>
    /// <param name="rooms">Configured room mappings.</param>
    /// <param name="update">Parsed occupancy update.</param>
    /// <returns><see langword="true"/> when the topic and payload identify a configured room.</returns>
    public static bool TryParseCount(
        string topic,
        string payload,
        IReadOnlyCollection<RoomOptions> rooms,
        out RoomOccupancyUpdate update,
        string topicPrefix = "frigate")
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rooms);

        update = new RoomOccupancyUpdate(string.Empty, 0);
        var room = rooms.FirstOrDefault(candidate =>
            string.Equals($"{topicPrefix}/{candidate.Zone ?? candidate.Camera}/person", topic, StringComparison.Ordinal));

        if (room is null ||
            !int.TryParse(payload, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 0)
        {
            return false;
        }

        update = new RoomOccupancyUpdate(room.Name, count);
        return true;
    }

    /// <summary>
    /// Parses the full camera activity response emitted after a Frigate on-connect request.
    /// </summary>
    /// <param name="payload">Frigate camera activity JSON.</param>
    /// <param name="rooms">Configured room mappings.</param>
    /// <param name="updates">Occupancy updates found in the snapshot.</param>
    /// <returns><see langword="true"/> when the JSON payload was valid.</returns>
    public static bool TryParseCameraActivity(
        string payload,
        IReadOnlyCollection<RoomOptions> rooms,
        out IReadOnlyList<RoomOccupancyUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rooms);

        var parsedUpdates = new List<RoomOccupancyUpdate>(rooms.Count);
        updates = parsedUpdates;

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var room in rooms)
            {
                if (!document.RootElement.TryGetProperty(room.Camera, out var camera) ||
                    !camera.TryGetProperty("objects", out var objects) ||
                    objects.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var count = 0;
                foreach (var trackedObject in objects.EnumerateArray())
                {
                    if (!IsPerson(trackedObject) || !IsInConfiguredZone(trackedObject, room.Zone))
                    {
                        continue;
                    }

                    count++;
                }

                parsedUpdates.Add(new RoomOccupancyUpdate(room.Name, count));
            }

            return true;
        }
        catch (JsonException)
        {
            parsedUpdates.Clear();
            return false;
        }
    }

    private static bool IsPerson(JsonElement trackedObject)
    {
        if (!trackedObject.TryGetProperty("label", out var labelElement))
        {
            return false;
        }

        var label = labelElement.GetString();
        return string.Equals(label, "person", StringComparison.Ordinal) ||
               string.Equals(label, "person-verified", StringComparison.Ordinal);
    }

    private static bool IsInConfiguredZone(JsonElement trackedObject, string? zone)
    {
        if (zone is null)
        {
            return true;
        }

        return trackedObject.TryGetProperty("current_zones", out var zones) &&
               zones.ValueKind == JsonValueKind.Array &&
               zones.EnumerateArray().Any(candidate =>
                   string.Equals(candidate.GetString(), zone, StringComparison.Ordinal));
    }
}

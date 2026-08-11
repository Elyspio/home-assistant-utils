using CSharpHomeAssistant.Apps.PresenceLighting;
using FluentAssertions;

namespace CSharpHomeAssistant.Tests;

[TestClass]
public sealed class FrigateOccupancyParserTests
{
    private static readonly RoomOptions Office = new()
    {
        Name = "office",
        Camera = "office",
        LightEntityId = "light.office"
    };

    [TestMethod]
    public void TryParseCount_accepts_numeric_person_counts()
    {
        // Act
        var parsed = FrigateOccupancyParser.TryParseCount(
            "frigate/office/person",
            "2",
            [Office],
            out var update);

        // Assert
        parsed.Should().BeTrue();
        update.Should().Be(new RoomOccupancyUpdate("office", 2));
    }

    [TestMethod]
    public void TryParseCount_rejects_active_person_topics()
    {
        // Act
        var parsed = FrigateOccupancyParser.TryParseCount(
            "frigate/office/person/active",
            "1",
            [Office],
            out _);

        // Assert
        parsed.Should().BeFalse();
    }

    [TestMethod]
    public void TryParseCount_uses_the_configured_topic_prefix()
    {
        var parsed = FrigateOccupancyParser.TryParseCount(
            "nvr/office/person",
            "1",
            [Office],
            out var update,
            "nvr");

        parsed.Should().BeTrue();
        update.Count.Should().Be(1);
    }

    [TestMethod]
    public void TryParseCameraActivity_counts_stationary_people()
    {
        // Arrange
        const string payload = """
            {
              "office": {
                "objects": [
                  { "label": "person", "stationary": true, "current_zones": [] },
                  { "label": "person", "stationary": false, "current_zones": [] }
                ]
              }
            }
            """;

        // Act
        var parsed = FrigateOccupancyParser.TryParseCameraActivity(payload, [Office], out var updates);

        // Assert
        parsed.Should().BeTrue();
        updates.Should().ContainSingle().Which.Should().Be(new RoomOccupancyUpdate("office", 2));
    }

    [TestMethod]
    public void TryParseCameraActivity_honors_optional_room_zone()
    {
        // Arrange
        var zonedOffice = new RoomOptions
        {
            Name = "office",
            Camera = "office",
            Zone = "desk",
            LightEntityId = "light.office"
        };
        const string payload = """
            {
              "office": {
                "objects": [
                  { "label": "person", "stationary": true, "current_zones": ["desk"] },
                  { "label": "person", "stationary": false, "current_zones": ["door"] }
                ]
              }
            }
            """;

        // Act
        var parsed = FrigateOccupancyParser.TryParseCameraActivity(payload, [zonedOffice], out var updates);

        // Assert
        parsed.Should().BeTrue();
        updates.Should().ContainSingle().Which.Should().Be(new RoomOccupancyUpdate("office", 1));
    }

    [TestMethod]
    public void TryParseCameraActivity_rejects_malformed_json()
    {
        // Act
        var parsed = FrigateOccupancyParser.TryParseCameraActivity("not-json", [Office], out var updates);

        // Assert
        parsed.Should().BeFalse();
        updates.Should().BeEmpty();
    }
}

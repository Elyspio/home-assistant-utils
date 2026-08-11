using System.Text.Json;
using CSharpHomeAssistant.Apps.PresenceLighting;
using FluentAssertions;

namespace CSharpHomeAssistant.Tests;

[TestClass]
public sealed class MqttDiscoveryTests
{
    [TestMethod]
    public void Discovery_exposes_global_controls_and_four_entities_per_room_with_unique_topics()
    {
        var options = new PresenceLightingOptions
        {
            Rooms =
            [
                new RoomOptions { Name = "office", Camera = "office", LightEntityId = "light.office" },
                new RoomOptions { Name = "kitchen", Camera = "kitchen", LightEntityId = "light.kitchen" }
            ]
        };

        var publications = MqttDiscovery.Build(options);

        publications.Should().HaveCount(11);
        publications.Select(publication => publication.Topic).Should().OnlyHaveUniqueItems();
        publications.Should().OnlyContain(publication => publication.Retain);
        publications.Should().Contain(publication => publication.Topic.Contains("/switch/") &&
            JsonDocument.Parse(publication.Payload).RootElement.GetProperty("command_topic").GetString() != null);
        publications.Should().Contain(publication => publication.Topic.Contains("office_presence"));
        publications.Should().Contain(publication => publication.Topic.Contains("kitchen_target"));
    }
}

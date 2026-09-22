using System.Text.Json;
using CSharpHomeAssistant.Apps.PresenceLighting;
using Shouldly;
using Xunit;

namespace CSharpHomeAssistant.Tests;

public sealed class MqttDiscoveryTests
{
    [Fact]
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

        publications.Count().ShouldBe(11);
        publications.Select(publication => publication.Topic).ShouldBeUnique();
        publications.ShouldAllBe(publication => publication.Retain);
        publications.ShouldContain(publication => publication.Topic.Contains("/switch/") &&
            JsonDocument.Parse(publication.Payload).RootElement.GetProperty("command_topic").GetString() != null);
        publications.ShouldContain(publication => publication.Topic.Contains("office_presence"));
        publications.ShouldContain(publication => publication.Topic.Contains("kitchen_target"));
    }
}

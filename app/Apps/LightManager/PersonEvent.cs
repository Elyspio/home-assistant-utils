using Newtonsoft.Json;

namespace CSharpHomeAssistant.Apps.LightManager;

public class PersonEvent
{
    [JsonProperty("entity_id")] public string EntityId { get; set; }

    [JsonProperty("old_state")] public PersonState OldState { get; set; }

    [JsonProperty("new_state")] public PersonState NewState { get; set; }
}

public class PersonState
{
    [JsonProperty("entity_id")] public string EntityId { get; set; }

    [JsonProperty("state")] public string State { get; set; }

    [JsonProperty("attributes")] public Attributes Attributes { get; set; }

    [JsonProperty("last_changed")] public DateTimeOffset LastChanged { get; set; }

    [JsonProperty("last_updated")] public DateTimeOffset LastUpdated { get; set; }

    [JsonProperty("context")] public Context Context { get; set; }
}

public class Attributes
{
    [JsonProperty("editable")] public bool Editable { get; set; }

    [JsonProperty("id")] public string Id { get; set; }

    [JsonProperty("latitude")] public double Latitude { get; set; }

    [JsonProperty("longitude")] public double Longitude { get; set; }

    [JsonProperty("gps_accuracy")] public long GpsAccuracy { get; set; }

    [JsonProperty("source")] public string Source { get; set; }

    [JsonProperty("user_id")] public string UserId { get; set; }

    [JsonProperty("entity_picture")] public string EntityPicture { get; set; }

    [JsonProperty("friendly_name")] public string FriendlyName { get; set; }
}

public class Context
{
    [JsonProperty("id")] public string Id { get; set; }

    [JsonProperty("parent_id")] public object ParentId { get; set; }

    [JsonProperty("user_id")] public object UserId { get; set; }
}
// Use unique namespaces for your apps if you going to share with others to avoid
// conflicting names

using NetDaemon.HassModel.Common;
using NetDaemon.HassModel.Entities;
using Newtonsoft.Json;

namespace CSharpHomeAssistant.Apps.LightManager;

public class PresenceManager
{
    private readonly IHaContext context;

    /// <summary>
    ///     If a person is at home or not
    /// </summary>
    private readonly Dictionary<string, Presence> presences = new();


    public PresenceManager(IHaContext ha)
    {
        context = ha;
        var persons = ha.GetAllEntities()
            .Where(ent => ent.EntityId.ToLower().Contains("person"))
            .ToList();


        persons.ForEach(person =>
        {
            var atHome = person.EntityState?.State == "home";
            presences[person.EntityId] = new Presence(atHome, atHome);
        });

        OnPresenceUpdate();

        ha.Events.Subscribe(evt =>
        {
            if (!(evt.DataElement?.TryGetProperty("entity_id", out var property) ?? false)) return;
            var entityId = property.GetString();
            if (entityId == null || !entityId.Contains("person")) return;

            Console.WriteLine($"Use event for entity {entityId}");

            if (evt.DataElement == null) return;

            var json = evt.DataElement.Value.GetRawText();
            var data = JsonConvert.DeserializeObject<PersonEvent>(json)!;

            presences[data.EntityId].Previous = presences[data.EntityId].Current;
            presences[data.EntityId].Current = data.NewState.State == "home";

            OnPresenceUpdate();
        });
    }


    public Action OnPresenceUpdate { get; set; } = () => { };


    public void TurnOffLights()
    {
        var lights = context.GetAllEntities()
            .Where(ent => ent.EntityId.ToLower().Contains("yeelight"))
            .ToList();

        foreach (var entity in lights)
        {
            context.CallService("light", "off", ServiceTarget.FromEntity(entity.EntityId));
            Console.WriteLine($"Light {entity.EntityId} turned off");
        }
    }


    public bool IsAllPersonAway()
    {
        return presences.All(pair => !pair.Value.Current);
    }

    public bool IsArrival()
    {
        return presences.All(pair => !pair.Value.Previous);
    }

    private record Presence(bool Previous, bool Current)
    {
        public bool Previous { get; set; } = Previous;
        public bool Current { get; set; } = Current;
    }
}
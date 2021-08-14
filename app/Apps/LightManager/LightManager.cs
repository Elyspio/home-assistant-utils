// Use unique namespaces for your apps if you going to share with others to avoid
// conflicting names

using NetDaemon.HassModel.Common;
using NetDaemon.HassModel.Entities;

namespace CSharpHomeAssistant.Apps.LightManager;

public class LightManager
{
    private readonly IHaContext context;

    public LightManager(IHaContext context)
    {
        this.context = context;
    }

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

    public void TurnOnEntry()
    {
        var entity = context.GetAllEntities()
            .First(ent => ent.Area == "Entrée");

        context.CallService("light", "on", ServiceTarget.FromEntity(entity.EntityId));
        Console.WriteLine($"Light {entity.EntityId} turned on");
    }
}
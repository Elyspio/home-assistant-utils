// Use unique namespaces for your apps if you going to share with others to avoid
// conflicting names

using NetDaemon.Common;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.HassModel.Common;

namespace CSharpHomeAssistant.Apps.LightManager;

[NetDaemonApp]
public class LightManagerApp
{
    private readonly LightManager lightManager;
    private readonly PresenceManager presenceManager;

    public LightManagerApp(IHaContext ha, INetDaemonScheduler scheduler)
    {
        lightManager = new LightManager(ha);
        presenceManager = new PresenceManager(ha)
        {
            OnPresenceUpdate = OnPresenceUpdate
        };
    }


    private void OnPresenceUpdate()
    {
        if (presenceManager.IsAllPersonAway())
        {
            Console.WriteLine("Everyone is away");
            lightManager.TurnOffLights();
        }

        if (presenceManager.IsArrival())
        {
            Console.WriteLine("It is an arrival, switching on entry");
            lightManager.TurnOnEntry();
        }
    }
}
# home-assistant-utils

NetDaemon application that combines Home Assistant lights with Frigate person counts over MQTT.

## Presence lighting

- Seven camera-to-light mappings are configured in `app/appsettings.json`.
- Frigate `person` counts include active and stationary people; `/person/active` is intentionally not used.
- A room-specific vacancy delay prevents short detection gaps from turning lights off.
- Lights already on receive a five-minute circadian refresh from 2200 K at night to 4000 K by day.
- External light changes open a one-hour manual override. A bedroom switched off at night stays latched until a confirmed vacancy.
- Home Assistant controls and diagnostics are published through MQTT Discovery.
- If Frigate or MQTT becomes unavailable, pending vacancy actions are cancelled and no light is turned off.

The MQTT master switch disables every command. Per-room switches disable presence control only; circadian updates continue. Vacation mode enables Frigate snapshots on all seven cameras and only confirms after all camera state topics agree.

## Development

```powershell
dotnet test --project tests/CSharpHomeAssistant.Tests/CSharpHomeAssistant.Tests.csproj
Push-Location deploy
docker buildx bake --push
Pop-Location
```

Keep Home Assistant and MQTT credentials in the ignored `app/appsettings.secrets.json` file. Kubernetes deployment files live in `common/keycloak/kubernetes/apps/home-assistant-utils`.

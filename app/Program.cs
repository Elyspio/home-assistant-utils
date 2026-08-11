using System.Reflection;
using CSharpHomeAssistant.Apps.PresenceLighting;
using CSharpHomeAssistant.Generated;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetDaemon.AppModel;
using NetDaemon.Runtime;

try
{
    Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

    await Host.CreateDefaultBuilder(args)
        .ConfigureLogging(logging => logging.AddSimpleConsole(x => x.SingleLine = true))
        .UseNetDaemonAppSettings()
        .ConfigureAppConfiguration((_, config) =>
            config.AddJsonFile("appsettings.secrets.json", optional: true, reloadOnChange: true))
        .ConfigureWebHostDefaults(webBuilder => webBuilder.Configure(app =>
        {
            app.Map("/health/live", branch => branch.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return context.Response.WriteAsync("live");
            }));
            app.Map("/health/ready", branch => branch.Run(context =>
            {
                var health = context.RequestServices.GetRequiredService<AutomationHealthState>();
                context.Response.StatusCode = health.Ready
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable;
                return context.Response.WriteAsync(health.Ready
                    ? "ready"
                    : "waiting for Home Assistant, MQTT, and Frigate synchronization");
            }));
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        }))
        .UseNetDaemonRuntime()
        .ConfigureServices((context, services) =>
        {
            services
                .AddAppsFromAssembly(Assembly.GetExecutingAssembly())
                .AddNetDaemonStateManager()
                .AddHomeAssistantGenerated();

            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<AutomationHealthState>();

            services
                .AddOptions<MqttOptions>()
                .Bind(context.Configuration.GetSection(MqttOptions.SectionName));

            services
                .AddOptions<PresenceLightingOptions>()
                .Bind(context.Configuration.GetSection(PresenceLightingOptions.SectionName));
        })
        .Build()
        .RunAsync()
        .ConfigureAwait(false);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Failed to start host: {exception}");
    Environment.ExitCode = 1;
}

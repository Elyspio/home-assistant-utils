using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NetDaemon;

try
{
    var builder = Host.CreateDefaultBuilder(args);

    builder
        .ConfigureAppConfiguration(config => config.AddJsonFile("appsettings.secrets.json"))
        .UseDefaultNetDaemonLogging()
        .UseNetDaemon()
        .Build()
        .Run();
}
catch (Exception e)
{
    Console.WriteLine($"Failed to start host... {e}");
}
finally
{
    NetDaemonExtensions.CleanupNetDaemon();
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IncludeDamon;

internal abstract class Program
{
    private static async Task Main()
    {
        try
        {
            await Host.CreateDefaultBuilder()
                .ConfigureServices((_, services) => services.AddHostedService<Service>())
                .Build()
                .RunAsync();
        }
        catch
        {
            // ignore
        }
    }
}

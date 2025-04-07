using BootstrapBlazor.Components;
using Revantage.YieldCurve.Services.Core;

namespace Revantage.Chart.Server.Extensions;

public static class ConfigureServiceExtensions
{
    public static IServiceCollection ConfigureRazorServices(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents()
            .AddInteractiveWebAssemblyComponents();
        services.AddRazorPages();
        return services;
    }

    public static IServiceCollection ConfigureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.ConfigureRazorServices();
        services.AddYieldCurveServiceCore(configuration);
        services.AddControllers();
        return services;
    }
}

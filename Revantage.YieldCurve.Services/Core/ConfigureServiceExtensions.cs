using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Revantage.YieldCurve.Services.Implementations;
using Revantage.YieldCurve.Services.Interfaces;

namespace Revantage.YieldCurve.Services.Core;

public static class ConfigureServiceExtensions
{
    public static IServiceCollection AddYieldCurveServiceCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions();
        services.AddBootstrapBlazor();

        services.AddScoped<IBondRateService, BondRateService>();
        return services;
    }
}

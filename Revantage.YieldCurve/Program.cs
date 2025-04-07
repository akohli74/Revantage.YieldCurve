using Microsoft.Extensions.Configuration;
using Revantage.Chart.Server.Extensions;
using Revantage.YieldCurve.Components;
using Revantage.YieldCurve.Services.Core;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var mySection = configuration.GetSection("ExternalServiceEndpoints");
builder.Services.Configure<ExternalServiceEndpointOptions>(options => mySection.Get<ExternalServiceEndpointOptions>());
builder.Services.ConfigureServices(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Revantage.YieldCurve.Client._Imports).Assembly);

app.Run();

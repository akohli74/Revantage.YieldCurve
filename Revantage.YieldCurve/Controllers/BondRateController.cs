using Microsoft.AspNetCore.Mvc;
using Revantage.YieldCurve.Services.Interfaces;
using Revantage.YieldCurve.Shared.Models;

namespace Revantage.YieldCurve.Server.Controllers;


[ApiController]
[Route("[controller]")]
public class BondRatesController : Controller
{
    private readonly IBondRateService _bondRateService;
    private readonly ILogger<BondRatesController> _logger;

    public BondRatesController(IBondRateService bondRateService, ILogger<BondRatesController> logger)
    {
        _bondRateService = bondRateService ?? throw new ArgumentNullException(nameof(bondRateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<BondRate>>> GetBondRatesAsync([FromQuery] DateTime asOfDate)
    {
        // hardcoding to 6/30/2023 for testing
        // asOfDate = new DateTime(2023, 06, 30);

        var date = new DateOnly(asOfDate.Year, asOfDate.Month, asOfDate.Day);
        var yieldCurve = await _bondRateService.BootstrapParRates(date);

        if (yieldCurve.DiscountFactors.Any())
        {
            IEnumerable<BondRate> bondRates = yieldCurve.DiscountFactors.Select(kvp => new BondRate
            {
                AsOfDate = yieldCurve.ParRates.First().Value.AsOfDate,
                Maturity = kvp.Key,
                Rate = yieldCurve.ParRates.ContainsKey(kvp.Key) ? yieldCurve.ParRates[kvp.Key].Rate / 100.0m : default,
                DiscountFactor = kvp.Value,
                AnnualizedZeroRate = yieldCurve.AnnualizedZeroRates.ContainsKey(kvp.Key) ? yieldCurve.AnnualizedZeroRates[kvp.Key] : default,
                MonthlyZeroRate = yieldCurve.MonthlyZeroRates.ContainsKey(kvp.Key) ? yieldCurve.MonthlyZeroRates[kvp.Key] : default,
                LogOfDiscountFactor = (decimal) Math.Log((double)kvp.Value)
            });

            return Ok(bondRates);
        }

        return NotFound(Enumerable.Empty<BondRate>());
    }
}

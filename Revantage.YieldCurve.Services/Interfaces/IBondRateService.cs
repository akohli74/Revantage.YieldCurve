using Revantage.YieldCurve.Shared.Models;

namespace Revantage.YieldCurve.Services.Interfaces;

public interface IBondRateService
{
    Task<Shared.Models.YieldCurve> BootstrapParRates(DateOnly asOfDate);
    BondRate InterpolateValues(DateOnly asOfDate, int month, double df);
    IEnumerable<BondRate> BootstrapInterpolation(DateOnly asOfDate, decimal parRateAnnual, decimal monthlyRate6, decimal df6, int targetMonth);
}

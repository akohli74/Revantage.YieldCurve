namespace Revantage.YieldCurve.Shared.Models;

public class YieldCurve
{
    public IDictionary<int, BondRate> ParRates { get; set; } = new Dictionary<int, BondRate>();
    public IDictionary<int, decimal> DiscountFactors { get; set; } = new Dictionary<int, decimal>();
    public IDictionary<int, decimal> MonthlyZeroRates { get; set; } = new Dictionary<int, decimal>();
    public IDictionary<int, decimal> AnnualizedZeroRates { get; set; } = new Dictionary<int, decimal>();
}

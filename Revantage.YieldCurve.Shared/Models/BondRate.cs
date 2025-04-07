namespace Revantage.YieldCurve.Shared.Models;

public class BondRate : IComparable<BondRate>
{
    public DateOnly AsOfDate { get; set; }
    public int Maturity { get; set; }
    public decimal Rate { get; set; }
    
    public decimal AnnualizedZeroRate { get; set; }
    
    public decimal MonthlyZeroRate { get; set; }
    
    public decimal DiscountFactor { get; set; }

    public decimal LogOfDiscountFactor { get; set; }

    public int CompareTo(BondRate? other)
    {
        if (other?.Maturity < this.Maturity) return 1;
        if (other?.Maturity > this.Maturity) return -1;
        return 0;
    }
}

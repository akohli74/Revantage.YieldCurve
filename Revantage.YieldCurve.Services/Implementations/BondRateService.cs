using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Web;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Revantage.YieldCurve.Services.Core;
using Revantage.YieldCurve.Services.Interfaces;
using Revantage.YieldCurve.Shared.Models;

namespace Revantage.YieldCurve.Services.Implementations;

public class BondRateService(IHttpClientFactory clientFactory, IOptions<ExternalServiceEndpointOptions> options, ILogger<BondRateService> logger, IConfiguration config)
    : IBondRateService
{
    private readonly HttpClient _httpClient = clientFactory.CreateClient() ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly ILogger<BondRateService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private IOptions<ExternalServiceEndpointOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IConfiguration _configuration = config ?? throw new ArgumentNullException(nameof(config));

    private IDictionary<int, BondRate> _bondRates = new Dictionary<int, BondRate>();

    private IDictionary<int, decimal> _discountFactors = new Dictionary<int, decimal>();
    private IDictionary<int, decimal> _monthlyZeroRates = new Dictionary<int, decimal>();
    private IDictionary<int, decimal> _annualizedZeroRates = new Dictionary<int, decimal>();
    public async Task<Shared.Models.YieldCurve> BootstrapParRates(DateOnly asOfDate)
    {
        ExternalServiceEndpointOptions options = new ExternalServiceEndpointOptions() { UsTreasuryPublicApiBaseUrl = string.Empty };
        if (_options.Value?.UsTreasuryPublicApiBaseUrl == null)
        {
            var mySection = _configuration.GetSection("ExternalServiceEndpoints");
            options = mySection.Get<ExternalServiceEndpointOptions>() ?? options;
        }
        var uri = new UriBuilder($"{_options.Value?.UsTreasuryPublicApiBaseUrl ?? options.UsTreasuryPublicApiBaseUrl}resource-center/data-chart-center/interest-rates/pages/xml");
        var query = HttpUtility.ParseQueryString(uri.Query);
        query["data"] = "daily_treasury_yield_curve";
        query["field_tdr_date_value_month"] = $"{asOfDate.Year:0000}{asOfDate.Month:00}";
        uri.Query = query.ToString();
        _httpClient.BaseAddress = new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}");
        var response = await _httpClient.GetAsync(uri.Uri.PathAndQuery).ConfigureAwait(false);
        var xml = await response.Content.ReadAsStringAsync();

        XDocument doc = XDocument.Parse(xml);
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace d = "http://schemas.microsoft.com/ado/2007/08/dataservices";
        XNamespace m = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";

        var properties = doc.Descendants(m + "properties");
        var rates = from entry in properties
                    where entry.Element(d + "NEW_DATE") != null && DateTime.TryParse(entry.Element(d + "NEW_DATE")?.Value, out var date)
                        && date.Year == asOfDate.Year && date.Month == asOfDate.Month && date.Day == asOfDate.Day
                    select entry;

        if (rates.Any())
        {



            var parRates = rates.Elements()                                 // skip 1.5 months
                .Where(e => e.Name.LocalName.StartsWith("BC_") && !(e.Name.LocalName.Where(r => r == '_').Count() == 2 && e.Name.LocalName.EndsWith("MONTH")))
                .Select<XElement, BondRate>(e =>
                {
                    var name = e.Name.LocalName;
                    if (!decimal.TryParse(e.Value, out var value))
                    {
                        _logger.LogError("Invalid bond rate value {value} for {name}", e.Value, name);
                        return new BondRate();
                    }

                    int months = name switch
                    {
                        var n when n.Where(r => r == '_').Count() == 2 && n.EndsWith("MONTH") => -1,
                        var n when n.EndsWith("MONTH") => int.Parse(n.Split('_')[1].Replace("MONTH", string.Empty)),
                        var n when n.EndsWith("YEAR") => int.Parse(n.Split('_')[1].Replace("YEAR", string.Empty)) * 12,
                        _ => -1
                    };

                    return new()
                    {
                        AsOfDate = asOfDate,
                        Rate = value,
                        Maturity = months
                    };
                })
                .Select(p => new KeyValuePair<int, BondRate>(p.Maturity, p))
                .ToDictionary();

            // ----------- Step 1. Bootstrap the monthly bonds (maturities: 1,2,3,4 months) -----------
            // For these bonds, the coupon is paid monthly.
            // Coupon per month = (annual par rate/12)*100.
            int[] monthlyMaturities = { 1, 2, 3, 4 };
            foreach (int tenor in monthlyMaturities)
            {
                decimal r = parRates[tenor].Rate / 100.0m;         // annual rate in decimal
                decimal coupon = r * 100.0m / 12.0m;           // monthly coupon payment (par = 100)
                decimal sum = 0.0m;
                // Sum coupon payments from earlier months
                for (int i = 1; i < tenor; i++)
                {
                    sum += coupon * _discountFactors[i];
                }
                // Price = 100 at par. Solve:
                // 100 = sum(coupon*DF(i)) + (coupon + 100)*DF(m)
                decimal df = (100.0m - sum) / (coupon + 100.0m);
                _discountFactors[tenor] = df;
                _logger.LogInformation($"{tenor}-month DF: {df:F5}");
            }

            // ----------- Step 2. Calculate the 6-month bond discount factor -----------
            // For the 6-month bond, with semiannual coupon:
            // Coupon per period = (parRate/2)*100.
            decimal r6 = parRates[6].Rate / 100.0m;
            decimal coupon6 = r6 * 100.0m / 2.0m;
            // Price = 100 = (coupon6+100)*DF(6)
            decimal df6 = 100.0m / (coupon6 + 100.0m);
            _discountFactors[6] = df6;
            _logger.LogInformation($"6-month DF: {df6:F5}");

            // ----------- Step 3. Interpolate the missing 5-month discount factor -----------
            // Assume a constant (geometric) forward rate between months 4 and 6:
            // DF(5) = sqrt(DF(4) * DF(6))
            decimal df5 = (decimal)Math.Sqrt((double)_discountFactors[4] * (double)_discountFactors[6]);
            _discountFactors[5] = df5;
            _logger.LogInformation($"5-month DF (interpolated): {df5:F5}");

            // ----------- Step 4. Bootstrap semiannual bonds (maturities: 12, 24, 36, 60 months) -----------
            // For these bonds, payments are semiannual.
            // We assume that between the last computed semiannual coupon date and the new bond’s maturity
            // the forward rate is constant and equals the new bond’s semiannual coupon rate.
            int lastComputedSemiPeriod = 1; // corresponds to the 6-month DF
            int lastComputedMonth = 6;        // last computed maturity in months
            int[] semiannualMaturities = { 12, 24, 36, 60, 84, 120, 240, 360 };
            foreach (int tenor in semiannualMaturities)
            {
                int n = tenor / 6;                  // number of semiannual periods
                decimal r = parRates[tenor].Rate / 100.0m;   // par rate for this bond (annualized)
                decimal couponSemi = r * 100.0m / 2.0m;  // coupon per semiannual period

                // For missing coupon dates from the last computed semiannual date to maturity,
                // assume DF(6*i) = DF(lastComputedMonth) / (1 + r/2)^(i - lastComputedSemiPeriod)
                for (int i = lastComputedSemiPeriod + 1; i <= n; i++)
                {
                    int periodMonth = i * 6;
                    double exponent = i - lastComputedSemiPeriod;
                    decimal dfValue = _discountFactors[lastComputedMonth] / (decimal)Math.Pow((double)(1 + r / 2.0m), exponent);
                    _discountFactors[periodMonth] = dfValue;
                    _logger.LogInformation($"{periodMonth}-month DF (bootstrapped): {dfValue:F5}");
                }
                lastComputedSemiPeriod = n;
                lastComputedMonth = tenor;
            }

            // ----------- Step 5. Calculate Zero Rates for each maturity -----------
            // For each maturity m (in months) with discount factor DF(m), compute:
            // Monthly zero rate: (1/DF(m))^(1/m) - 1.
            // Annual zero rate: (1/DF(m))^(12/m) - 1.
            foreach (var kvp in new SortedDictionary<int, decimal>(_discountFactors))
            {
                int tenor = kvp.Key;
                decimal df = kvp.Value;
                decimal zeroMonthly = (decimal)Math.Pow((double)(1.0m / df), (double)(1.0m / tenor)) - 1.0m;
                decimal zeroAnnual = (decimal)Math.Pow((double)(1.0m / df), (double)(12.0m / tenor)) - 1.0m;
                _monthlyZeroRates[tenor] = zeroMonthly;
                _annualizedZeroRates[tenor] = zeroAnnual;
                _logger.LogInformation($"{tenor}-month: Zero Monthly Rate = {(zeroMonthly * 100):F4}%, Zero Annual Rate = {(zeroAnnual * 100):F4}%");
            }

            // ----------- (Optional) Display all Discount Factors -----------
            Console.WriteLine("\nAll Discount Factors:");
            foreach (var kvp in new SortedDictionary<int, decimal>(_discountFactors))
            {
                Console.WriteLine($"{kvp.Key}-month DF: {kvp.Value:F5}");
            }

            return new Shared.Models.YieldCurve()
            {
                ParRates = parRates,
                AnnualizedZeroRates = _annualizedZeroRates,
                MonthlyZeroRates = _monthlyZeroRates,
                DiscountFactors = _discountFactors
            };
        }

        return new();
    }

    private BondRate Get6MonthRates(DateOnly asOfDate, decimal value, int months)
    {
        double coupon = (double)value / 100;
        double numerator = 1 + (0.5 * coupon);
        double onePlusX = Math.Pow(numerator, 1.0 / 6.0);
        double x = onePlusX - 1;

        double discountFactor = 1 / Math.Pow(1 + x, months);

        return new BondRate()
        {
            AsOfDate = asOfDate,
            Maturity = months,
            MonthlyZeroRate = (decimal)x,
            AnnualizedZeroRate = ((decimal)Math.Pow(1d + x, 12d)) - 1,
            DiscountFactor = (decimal)discountFactor,
            LogOfDiscountFactor = (decimal)Math.Log(discountFactor),
            Rate = (decimal)coupon,
        };
    }

    public IEnumerable<BondRate> BootstrapInterpolation(DateOnly asOfDate, decimal parRateAnnual, decimal monthlyRate6, decimal df6, int targetMonth)
    {
        IEnumerable<BondRate> rates = new List<BondRate>();

        decimal semiAnnualCoupon = parRateAnnual / 2.0m; // 0.0222
        decimal price = 1.0m;

        // Step 1: Bootstrap DF for 12 months (1 year par bond: 0.0222 + 1.0 paid at maturity)
        // price = 0.0222 * DF6 + (1.0 + 0.0222) * DF12
        decimal cashflow12 = 1.0m + semiAnnualCoupon;
        decimal df12 = (price - (semiAnnualCoupon * df6)) / cashflow12;

        // Step 2: Interpolate DF for months 7–11 using log-linear method
        var discountFactors = new Dictionary<int, decimal>();
        discountFactors[6] = df6;
        discountFactors[12] = df12;

        for (int m = 7; m <= 11; m++)
        {
            double t = m / 12.0; // time in years
            double logDf6 = Math.Log((double)df6);
            double logDf12 = Math.Log((double)df12);
            double logDfm = logDf6 + ((m - 6) / 6.0) * (logDf12 - logDf6);
            decimal dfm = (decimal)Math.Exp(logDfm);
            discountFactors[m] = dfm;

            // Compute zero rates
            double annualZeroRate = -Math.Log((double)dfm) / t;
            double monthlyZeroRate = annualZeroRate / 12.0;
            rates = rates.Append(new BondRate()
            {
                AsOfDate = asOfDate,
                DiscountFactor = (decimal)dfm,
                AnnualizedZeroRate = (decimal)annualZeroRate,
                MonthlyZeroRate = (decimal)monthlyZeroRate,
                Maturity = m,
                LogOfDiscountFactor = (decimal)Math.Log((double)dfm)
            });
        }

        return rates;
    }

    public BondRate InterpolateValues(DateOnly asOfDate, int month, double df)
    {
        BondRate bondRate = new BondRate();

        double df6 = df;
        double t7 = month / 12.0d;

        // Step 1: Interpolate next DF
        double logDf6 = Math.Log(df6);
        double logDf7 = (month / 6.0) * logDf6;
        double df7 = Math.Exp(logDf7);

        // Step 2: Calculate the annual zero rate
        double annualZeroRate = -Math.Log(df7) / t7;

        // Step 3: Convert to monthly zero rate
        double monthlyZeroRate = annualZeroRate / 12.0d;

        bondRate.AsOfDate = asOfDate;
        bondRate.DiscountFactor = (decimal)df7;
        bondRate.AnnualizedZeroRate = (decimal)annualZeroRate;
        bondRate.MonthlyZeroRate = (decimal)monthlyZeroRate;
        bondRate.Maturity = month;
        bondRate.LogOfDiscountFactor = (decimal)Math.Log(df7);

        return bondRate;
    }

    public IEnumerable<BondRate> InterpolateValues2(DateOnly asOfDate, int months, double df)
    {
        double t6 = (double)months / 12;
        IList<BondRate> bondRates = new List<BondRate>();
        for (int m = months; m < months + 5; m++)
        {
            double logDf = Math.Log(df);

            double t = m / 12.0;
            double logDfm = (t / t6) * logDf;
            double dfm = Math.Exp(logDfm);

            double annualZeroRate = -Math.Log(dfm) / t;
            double monthlyZeroRate = annualZeroRate / 12.0;

            bondRates.Add(new BondRate()
            {
                AsOfDate = asOfDate,
                DiscountFactor = (decimal)dfm,
                AnnualizedZeroRate = (decimal)annualZeroRate,
                MonthlyZeroRate = (decimal)monthlyZeroRate,
                Maturity = m,
                LogOfDiscountFactor = (decimal)Math.Log(dfm)
            });

            df = dfm;
        }

        return bondRates;
    }

    private BondRate Get12MonthRates(DateOnly asOfDate, decimal value, int months)
    {
        double coupon = (double)value / 100d;
        double knownRate = (double)_bondRates[months - 6].MonthlyZeroRate;

        double term1Numerator = 0.5 * coupon; // 0.5 * 0.0540
        double term1Denominator = Math.Pow(1 + knownRate, 6);
        double term1 = term1Numerator / term1Denominator;

        double remaining = 1.0 - term1;
        double rhsNumerator = (0.5 * coupon) + 1;
        double rhsDenominator = rhsNumerator / remaining;

        double onePlusX = Math.Pow(rhsDenominator, 1.0d / 12.0d);

        double x = onePlusX - 1;

        double discountFactor = 1 / Math.Pow(1d + x, months);

        return new BondRate()
        {
            AsOfDate = asOfDate,
            Maturity = months,
            MonthlyZeroRate = (decimal)x,
            AnnualizedZeroRate = (decimal)Math.Pow(1 + x, 12) - 1,
            DiscountFactor = (decimal)discountFactor,
            LogOfDiscountFactor = (decimal)Math.Log(discountFactor),
            Rate = (decimal)coupon,
        };
    }
}

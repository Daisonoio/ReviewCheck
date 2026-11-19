namespace Sample.Pricing;

/// <summary>Applies discounts to prices, honouring <see cref="PricingRules"/>.</summary>
public class DiscountCalculator
{
    /// <summary>Applies the requested percentage off the price, capped by the pricing rules.</summary>
    public decimal ApplyDiscount(decimal price, int requestedPercent)
    {
        var percent = PricingRules.Clamp(requestedPercent);
        return price - price * percent / 100m;
    }
}

namespace Sample.Pricing;

/// <summary>Rules that constrain how discounts may be applied.</summary>
public static class PricingRules
{
    /// <summary>The largest discount, as a percentage, that may ever be applied.</summary>
    public const int MaxDiscountPercent = 50;

    /// <summary>Clamps a requested discount percentage into the allowed range [0, MaxDiscountPercent].</summary>
    public static int Clamp(int percent) =>
        percent < 0 ? 0 : percent > MaxDiscountPercent ? MaxDiscountPercent : percent;
}

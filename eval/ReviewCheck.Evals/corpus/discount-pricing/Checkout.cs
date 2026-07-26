namespace Sample.Pricing;

/// <summary>Computes the amount due for a basket, applying a discount through the calculator.</summary>
public class Checkout
{
    private readonly DiscountCalculator _calculator = new();

    /// <summary>Returns the price after the discount the calculator computes.</summary>
    public decimal AmountDue(decimal listPrice, int discountPercent) =>
        _calculator.ApplyDiscount(listPrice, discountPercent);
}

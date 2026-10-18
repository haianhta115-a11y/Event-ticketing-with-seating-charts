using EventTicketing.Api.Entities;

namespace EventTicketing.Api.Services;

public static class OrderCalculator
{
    public static decimal CalculateTotal(IEnumerable<OrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Sum(item => item.UnitPrice);
    }

    public static decimal CalculateTotalFromPrices(IEnumerable<decimal> prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return prices.Sum();
    }
}

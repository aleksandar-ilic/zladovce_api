using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Domain.Common;
using Zladovce.Domain.Orders;

namespace Zladovce.Application.Analytics;

/// <summary>
/// The P1.8 numbers of one period. All money is RSD with two decimals.
/// </summary>
/// <param name="OrderedValue">Poručena vrednost: totals of orders placed in the period whose current status is not Rejected or Cancelled.</param>
/// <param name="OrderedCount">Orders counted in <paramref name="OrderedValue"/>.</param>
/// <param name="DeliveredValue">Vrednost isporučenih: totals of orders whose Delivered transition is in the period.</param>
/// <param name="DeliveredCount">Orders delivered in the period.</param>
/// <param name="EstimatedCollected">Procenjena naplata = delivered value (cash on delivery is assumed collected at delivery).</param>
/// <param name="ActualCollected">Stvarno naplaćeno: amounts recorded as received from the courier, by the date received.</param>
/// <param name="CollectedCount">Orders whose collection date is in the period.</param>
/// <param name="Expenses">Troškovi entered for days of the period.</param>
/// <param name="EstimatedProfit">Procenjena dobit = delivered value − expenses (only as complete as the expenses entered).</param>
/// <param name="AverageOrderValue">Ordered value / ordered count, rounded to two decimals; null when no order was counted.</param>
/// <param name="CancelledCount">Orders that became Cancelled in the period (transition time).</param>
/// <param name="RejectedCount">Orders that became Rejected in the period (transition time).</param>
public sealed record PeriodMetrics(
    decimal OrderedValue,
    int OrderedCount,
    decimal DeliveredValue,
    int DeliveredCount,
    decimal EstimatedCollected,
    decimal ActualCollected,
    int CollectedCount,
    decimal Expenses,
    decimal EstimatedProfit,
    decimal? AverageOrderValue,
    int CancelledCount,
    int RejectedCount)
{
    public int CancelledOrRejectedCount => CancelledCount + RejectedCount;
}

/// <summary>Orders waiting for the store right now (current status, independent of the period).</summary>
/// <param name="AwaitingConfirmation">"Čeka potvrdu": status Received (Primljena).</param>
/// <param name="Accepted">"Prihvaćene": status Accepted.</param>
/// <param name="Preparing">"U pripremi": status Preparing.</param>
/// <param name="InPreparation">Accepted + Preparing: everything accepted but not yet shipped.</param>
/// <param name="AwaitingShipment">"Čeka slanje": status Preparing (packed, waiting for the courier).</param>
/// <param name="DeliveryFailed">"Neuspešna isporuka": waiting for a decision.</param>
public sealed record OrderQueues(int AwaitingConfirmation, int Accepted, int Preparing, int InPreparation, int AwaitingShipment, int DeliveryFailed);

/// <summary>Response of <c>GET /api/v1/admin/dashboard/summary</c>.</summary>
/// <param name="Current">The selected period.</param>
/// <param name="Previous">The comparable previous period, for trend arrows.</param>
public sealed record DashboardSummaryResponse(PeriodInfo Period, PeriodMetrics Current, PeriodMetrics Previous, OrderQueues Queues, string Currency);

/// <param name="From">First day of the bucket (Serbian local date).</param>
/// <param name="To">Last day of the bucket, inclusive (a month bucket is cut to the period).</param>
/// <param name="Label"><c>yyyy-MM-dd</c> (day) or <c>yyyy-MM</c> (month).</param>
public sealed record RevenuePoint(DateOnly From, DateOnly To, string Label, decimal OrderedValue, decimal DeliveredValue);

public sealed record RevenueSeriesResponse(PeriodInfo Period, IReadOnlyList<RevenuePoint> Points, string Currency);

/// <param name="OrderCount">Orders placed in the bucket, without Rejected and Cancelled (the orders of the ordered value).</param>
/// <param name="PlacedCount">All orders placed in the bucket.</param>
public sealed record OrderCountPoint(DateOnly From, DateOnly To, string Label, int OrderCount, int PlacedCount);

public sealed record OrderSeriesResponse(PeriodInfo Period, IReadOnlyList<OrderCountPoint> Points);

/// <summary>A product's sales from delivered orders.</summary>
/// <param name="ProductId">Current product of the sold variants.</param>
/// <param name="ProductName">Name at the time of the latest sale (order item snapshot).</param>
/// <param name="Revenue">Sum of line totals (without shipping).</param>
public sealed record TopProductItem(Guid ProductId, string ProductName, int Quantity, decimal Revenue);

public sealed record TopProductsResponse(PeriodInfo Period, IReadOnlyList<TopProductItem> ByQuantity, IReadOnlyList<TopProductItem> ByRevenue, string Currency);

/// <param name="CategoryId">Null for "Bez kategorije" (the product's category is archived).</param>
public sealed record CategorySalesItem(Guid? CategoryId, string CategoryName, int Quantity, decimal Revenue);

public sealed record SalesByCategoryResponse(PeriodInfo Period, IReadOnlyList<CategorySalesItem> Items, decimal TotalRevenue, string Currency);

public sealed record StatusCountItem(OrderStatus Status, string Label, int Count);

/// <param name="Items">All eight statuses (zero included) in state-machine order.</param>
public sealed record StatusDistributionResponse(PeriodInfo Period, IReadOnlyList<StatusCountItem> Items, int Total);

public interface IDashboardService
{
    Task<DashboardSummaryResponse> GetSummaryAsync(AnalyticsPeriod period, CancellationToken cancellationToken);
    Task<RevenueSeriesResponse> GetRevenueSeriesAsync(AnalyticsPeriod period, CancellationToken cancellationToken);
    Task<OrderSeriesResponse> GetOrderSeriesAsync(AnalyticsPeriod period, CancellationToken cancellationToken);
    Task<TopProductsResponse> GetTopProductsAsync(AnalyticsPeriod period, int limit, CancellationToken cancellationToken);
    Task<SalesByCategoryResponse> GetSalesByCategoryAsync(AnalyticsPeriod period, CancellationToken cancellationToken);
    Task<StatusDistributionResponse> GetStatusDistributionAsync(AnalyticsPeriod period, CancellationToken cancellationToken);
}

/// <summary>
/// Dashboard numbers from real data only (P1.8). Rows of the period are read with indexed range queries and bucketed in memory
/// by their Serbian local date, so day boundaries follow Europe/Belgrade including the DST change days.
/// </summary>
public sealed class DashboardService(IZladovceDbContext db) : IDashboardService
{
    public const string NoCategoryName = "Bez kategorije";

    private static readonly OrderStatus[] NotCounted = [OrderStatus.Rejected, OrderStatus.Cancelled];

    public async Task<DashboardSummaryResponse> GetSummaryAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var current = await MetricsAsync(period.Current, cancellationToken);
        var previous = await MetricsAsync(period.Previous, cancellationToken);

        var counts = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Received || o.Status == OrderStatus.Accepted || o.Status == OrderStatus.Preparing
                || o.Status == OrderStatus.DeliveryFailed)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);
        int Count(OrderStatus s) => counts.GetValueOrDefault(s);

        var queues = new OrderQueues(
            Count(OrderStatus.Received),
            Count(OrderStatus.Accepted),
            Count(OrderStatus.Preparing),
            Count(OrderStatus.Accepted) + Count(OrderStatus.Preparing),
            Count(OrderStatus.Preparing),
            Count(OrderStatus.DeliveryFailed));

        return new DashboardSummaryResponse(PeriodInfo.From(period), current, previous, queues, Money.Currency);
    }

    public async Task<RevenueSeriesResponse> GetRevenueSeriesAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var placed = await PlacedAsync(period.Current, cancellationToken);
        var delivered = await DeliveredAsync(period.Current, cancellationToken);
        var points = period.Buckets().Select(b => new RevenuePoint(
            b.From,
            b.To,
            Label(period, b),
            M(placed.Where(o => b.Contains(o.PlacedAt) && !NotCounted.Contains(o.Status)).Sum(o => o.Total)),
            M(delivered.Where(d => b.Contains(d.DeliveredAt)).Sum(d => d.Total)))).ToList();
        return new RevenueSeriesResponse(PeriodInfo.From(period), points, Money.Currency);
    }

    public async Task<OrderSeriesResponse> GetOrderSeriesAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var placed = await PlacedAsync(period.Current, cancellationToken);
        var points = period.Buckets().Select(b => new OrderCountPoint(
            b.From,
            b.To,
            Label(period, b),
            placed.Count(o => b.Contains(o.PlacedAt) && !NotCounted.Contains(o.Status)),
            placed.Count(o => b.Contains(o.PlacedAt)))).ToList();
        return new OrderSeriesResponse(PeriodInfo.From(period), points);
    }

    public async Task<TopProductsResponse> GetTopProductsAsync(AnalyticsPeriod period, int limit, CancellationToken cancellationToken)
    {
        var lines = await DeliveredLinesAsync(period.Current, cancellationToken);
        var products = lines
            .GroupBy(l => l.ProductId)
            .Select(g => new TopProductItem(
                g.Key,
                g.OrderByDescending(l => l.DeliveredAt).ThenBy(l => l.ProductName, StringComparer.Ordinal).First().ProductName,
                g.Sum(l => l.Quantity),
                M(g.Sum(l => l.LineTotal))))
            .ToList();

        return new TopProductsResponse(
            PeriodInfo.From(period),
            [.. products.OrderByDescending(p => p.Quantity).ThenByDescending(p => p.Revenue).ThenBy(p => p.ProductName, StringComparer.Ordinal).Take(limit)],
            [.. products.OrderByDescending(p => p.Revenue).ThenByDescending(p => p.Quantity).ThenBy(p => p.ProductName, StringComparer.Ordinal).Take(limit)],
            Money.Currency);
    }

    public async Task<SalesByCategoryResponse> GetSalesByCategoryAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var lines = await DeliveredLinesAsync(period.Current, cancellationToken);
        var items = lines
            .GroupBy(l => l.CategoryId)
            .Select(g => new CategorySalesItem(g.Key, g.First().CategoryName, g.Sum(l => l.Quantity), M(g.Sum(l => l.LineTotal))))
            .OrderByDescending(i => i.Revenue).ThenBy(i => i.CategoryName, StringComparer.Ordinal)
            .ToList();
        return new SalesByCategoryResponse(PeriodInfo.From(period), items, M(items.Sum(i => i.Revenue)), Money.Currency);
    }

    public async Task<StatusDistributionResponse> GetStatusDistributionAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var placed = await PlacedAsync(period.Current, cancellationToken);
        var items = Enum.GetValues<OrderStatus>()
            .Select(s => new StatusCountItem(s, OrderStatusLabels.Of(s), placed.Count(o => o.Status == s)))
            .ToList();
        return new StatusDistributionResponse(PeriodInfo.From(period), items, placed.Count);
    }

    // ---- data ----

    internal sealed record PlacedOrder(DateTimeOffset PlacedAt, decimal Total, OrderStatus Status);

    internal sealed record DeliveredOrder(Guid OrderId, DateTimeOffset DeliveredAt, decimal Total);

    /// <summary>One order item of an order delivered in the period, with the current product and category.</summary>
    public sealed record DeliveredLine(
        DateTimeOffset DeliveredAt,
        Guid VariantId,
        Guid ProductId,
        string ProductName,
        string VariantName,
        string Sku,
        int Quantity,
        decimal LineTotal,
        Guid? CategoryId,
        string CategoryName);

    private async Task<PeriodMetrics> MetricsAsync(DateRange range, CancellationToken cancellationToken)
    {
        var placed = await PlacedAsync(range, cancellationToken);
        var delivered = await DeliveredAsync(range, cancellationToken);

        var (start, end) = (range.StartUtc, range.EndUtc);
        var collected = await db.Orders.AsNoTracking()
            .Where(o => o.CollectedAt >= start && o.CollectedAt < end && o.CollectedAmount != null)
            .Select(o => o.CollectedAmount!.Value)
            .ToListAsync(cancellationToken);

        var (from, to) = (range.From, range.To);
        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => e.ExpenseDate >= from && e.ExpenseDate <= to)
            .SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0m;

        var endings = await db.OrderStatusHistory.AsNoTracking()
            .Where(h => (h.NewStatus == OrderStatus.Cancelled || h.NewStatus == OrderStatus.Rejected) && h.CreatedAt >= start && h.CreatedAt < end)
            .GroupBy(h => h.NewStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var counted = placed.Where(o => !NotCounted.Contains(o.Status)).ToList();
        var orderedValue = counted.Sum(o => o.Total);
        var deliveredValue = delivered.Sum(d => d.Total);

        return new PeriodMetrics(
            M(orderedValue),
            counted.Count,
            M(deliveredValue),
            delivered.Count,
            M(deliveredValue),
            M(collected.Sum()),
            collected.Count,
            M(expenses),
            M(deliveredValue - expenses),
            counted.Count == 0 ? null : M(orderedValue / counted.Count),
            endings.GetValueOrDefault(OrderStatus.Cancelled),
            endings.GetValueOrDefault(OrderStatus.Rejected));
    }

    private async Task<List<PlacedOrder>> PlacedAsync(DateRange range, CancellationToken cancellationToken)
    {
        var (start, end) = (range.StartUtc, range.EndUtc);
        return await db.Orders.AsNoTracking()
            .Where(o => o.PlacedAt >= start && o.PlacedAt < end)
            .Select(o => new PlacedOrder(o.PlacedAt, o.Total, o.Status))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<DeliveredOrder>> DeliveredAsync(DateRange range, CancellationToken cancellationToken)
    {
        var (start, end) = (range.StartUtc, range.EndUtc);
        return await (
                from h in db.OrderStatusHistory.AsNoTracking()
                where h.NewStatus == OrderStatus.Delivered && h.CreatedAt >= start && h.CreatedAt < end
                join o in db.Orders.AsNoTracking() on h.OrderId equals o.Id
                select new DeliveredOrder(o.Id, h.CreatedAt, o.Total))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Items of orders delivered in the range. Also used by the sales report.</summary>
    public async Task<List<DeliveredLine>> DeliveredLinesAsync(DateRange range, CancellationToken cancellationToken)
    {
        var (start, end) = (range.StartUtc, range.EndUtc);
        var rows = await (
                from h in db.OrderStatusHistory.AsNoTracking()
                where h.NewStatus == OrderStatus.Delivered && h.CreatedAt >= start && h.CreatedAt < end
                join i in db.OrderItems.AsNoTracking() on h.OrderId equals i.OrderId
                join v in db.ProductVariants.AsNoTracking() on i.VariantId equals v.Id
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                select new
                {
                    h.CreatedAt,
                    i.VariantId,
                    p.Id,
                    i.ProductName,
                    i.VariantName,
                    i.Sku,
                    i.Quantity,
                    i.LineTotal,
                    CategoryId = c.Id,
                    CategoryName = c.Name,
                    CategoryArchived = c.DeletedAt != null,
                })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new DeliveredLine(
            r.CreatedAt,
            r.VariantId,
            r.Id,
            r.ProductName,
            r.VariantName,
            r.Sku,
            r.Quantity,
            r.LineTotal,
            r.CategoryArchived ? null : r.CategoryId,
            r.CategoryArchived ? NoCategoryName : r.CategoryName))];
    }

    /// <summary>RSD with two decimals (also in the JSON: 3350.00, not 3350).</summary>
    internal static decimal M(decimal value) => Money.Round(value) + 0.00m;

    private static string Label(AnalyticsPeriod period, DateRange bucket) =>
        period.Granularity == SeriesGranularity.Day
            ? bucket.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : bucket.From.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}

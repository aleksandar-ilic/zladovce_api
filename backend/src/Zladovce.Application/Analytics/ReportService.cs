using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Application.Inventory;
using Zladovce.Domain.Common;
using Zladovce.Domain.Inventory;

namespace Zladovce.Application.Analytics;

/// <summary>Sales of one variant (order item snapshots of delivered orders).</summary>
/// <param name="Revenue">Sum of line totals, without shipping.</param>
public sealed record SalesByVariantRow(
    Guid VariantId, Guid ProductId, string ProductName, string VariantName, string Sku, string CategoryName, int Quantity, decimal Revenue);

/// <summary>Response of <c>GET /api/v1/admin/reports/sales</c>.</summary>
/// <param name="ByVariant">Sorted by product name, then variant name.</param>
/// <param name="ByCategory">Sorted by revenue, highest first.</param>
/// <param name="DeliveredOrders">Orders whose Delivered transition is in the period.</param>
/// <param name="ShippingRevenue">Shipping charged on those orders (not part of the product revenue).</param>
public sealed record SalesReportResponse(
    PeriodInfo Period,
    IReadOnlyList<SalesByVariantRow> ByVariant,
    IReadOnlyList<CategorySalesItem> ByCategory,
    int TotalQuantity,
    decimal TotalRevenue,
    int DeliveredOrders,
    decimal ShippingRevenue,
    string Currency);

/// <param name="Status"><c>ok</c>, <c>low</c> or <c>out</c>.</param>
/// <param name="StatusLabel">Na stanju, Malo na stanju, Nema na stanju.</param>
/// <param name="StockValue">Quantity on hand × current regular price.</param>
public sealed record InventoryReportRow(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string VariantName,
    string Sku,
    string CategoryName,
    int QuantityOnHand,
    int LowStockThreshold,
    StockStatus Status,
    string StatusLabel,
    decimal RegularPrice,
    decimal StockValue,
    bool IsActive);

/// <summary>Response of <c>GET /api/v1/admin/reports/inventory</c>: every variant that is not archived.</summary>
public sealed record InventoryReportResponse(IReadOnlyList<InventoryReportRow> Items, int TotalQuantity, decimal TotalStockValue, DateTimeOffset GeneratedAt, string Currency);

public enum SalesReportGrouping
{
    Variant,
    Category,
}

public interface IReportService
{
    Task<SalesReportResponse> GetSalesAsync(AnalyticsPeriod period, CancellationToken cancellationToken);
    Task<CsvFile> GetSalesCsvAsync(AnalyticsPeriod period, SalesReportGrouping grouping, CancellationToken cancellationToken);
    Task<InventoryReportResponse> GetInventoryAsync(CancellationToken cancellationToken);
    Task<CsvFile> GetInventoryCsvAsync(CancellationToken cancellationToken);
}

public sealed class ReportService(IZladovceDbContext db, TimeProvider timeProvider) : IReportService
{
    public static string StockStatusLabel(StockStatus status) => status switch
    {
        StockStatus.Ok => "Na stanju",
        StockStatus.Low => "Malo na stanju",
        _ => "Nema na stanju",
    };

    public async Task<SalesReportResponse> GetSalesAsync(AnalyticsPeriod period, CancellationToken cancellationToken)
    {
        var lines = await new DashboardService(db).DeliveredLinesAsync(period.Current, cancellationToken);

        var byVariant = lines
            .GroupBy(l => l.VariantId)
            .Select(g =>
            {
                var latest = g.OrderByDescending(l => l.DeliveredAt).First();
                return new SalesByVariantRow(g.Key, latest.ProductId, latest.ProductName, latest.VariantName, latest.Sku, latest.CategoryName,
                    g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal));
            })
            .OrderBy(r => r.ProductName, StringComparer.Ordinal).ThenBy(r => r.VariantName, StringComparer.Ordinal).ThenBy(r => r.Sku, StringComparer.Ordinal)
            .ToList();

        var byCategory = lines
            .GroupBy(l => l.CategoryId)
            .Select(g => new CategorySalesItem(g.Key, g.First().CategoryName, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotal)))
            .OrderByDescending(i => i.Revenue).ThenBy(i => i.CategoryName, StringComparer.Ordinal)
            .ToList();

        var (start, end) = (period.Current.StartUtc, period.Current.EndUtc);
        var delivered = await (
                from h in db.OrderStatusHistory.AsNoTracking()
                where h.NewStatus == Domain.Orders.OrderStatus.Delivered && h.CreatedAt >= start && h.CreatedAt < end
                join o in db.Orders.AsNoTracking() on h.OrderId equals o.Id
                select o.ShippingPrice)
            .ToListAsync(cancellationToken);

        return new SalesReportResponse(
            PeriodInfo.From(period), byVariant, byCategory, lines.Sum(l => l.Quantity), DashboardService.M(lines.Sum(l => l.LineTotal)),
            delivered.Count, DashboardService.M(delivered.Sum()), Money.Currency);
    }

    public async Task<CsvFile> GetSalesCsvAsync(AnalyticsPeriod period, SalesReportGrouping grouping, CancellationToken cancellationToken)
    {
        var report = await GetSalesAsync(period, cancellationToken);
        var csv = new CsvWriter();
        if (grouping == SalesReportGrouping.Category)
        {
            csv.Row("Kategorija", "Količina", "Prihod (RSD)");
            foreach (var row in report.ByCategory)
            {
                csv.Text(row.CategoryName).Number(row.Quantity).Number(row.Revenue).EndRow();
            }

            csv.Text("Ukupno").Number(report.TotalQuantity).Number(report.TotalRevenue).EndRow();
        }
        else
        {
            csv.Row("Proizvod", "Varijanta", "Šifra (SKU)", "Kategorija", "Količina", "Prihod (RSD)");
            foreach (var row in report.ByVariant)
            {
                csv.Text(row.ProductName).Text(row.VariantName).Text(row.Sku).Text(row.CategoryName).Number(row.Quantity).Number(row.Revenue).EndRow();
            }

            csv.Text("Ukupno").Text(null).Text(null).Text(null).Number(report.TotalQuantity).Number(report.TotalRevenue).EndRow();
        }

        var name = grouping == SalesReportGrouping.Category ? "prodaja-po-kategorijama" : "prodaja-po-proizvodima";
        return new CsvFile($"{name}-{period.Current.From:yyyy-MM-dd}-{period.Current.To:yyyy-MM-dd}.csv", csv.ToBytes());
    }

    public async Task<InventoryReportResponse> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from v in db.ProductVariants.AsNoTracking()
                where v.DeletedAt == null
                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                where p.DeletedAt == null
                join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                join i in db.Inventory.AsNoTracking() on v.Id equals i.VariantId
                orderby p.Name, v.SortOrder, v.Name, v.Id
                select new
                {
                    VariantId = v.Id,
                    ProductId = p.Id,
                    ProductName = p.Name,
                    VariantName = v.Name,
                    v.Sku,
                    CategoryName = c.DeletedAt == null ? c.Name : DashboardService.NoCategoryName,
                    i.QuantityOnHand,
                    i.LowStockThreshold,
                    v.RegularPrice,
                    IsActive = v.IsActive && p.IsActive,
                })
            .ToListAsync(cancellationToken);

        var items = rows.Select(r =>
        {
            var status = StockRules.StatusOf(r.QuantityOnHand, r.LowStockThreshold);
            return new InventoryReportRow(r.VariantId, r.ProductId, r.ProductName, r.VariantName, r.Sku, r.CategoryName, r.QuantityOnHand,
                r.LowStockThreshold, status, StockStatusLabel(status), r.RegularPrice, r.QuantityOnHand * r.RegularPrice, r.IsActive);
        }).ToList();

        return new InventoryReportResponse(items, items.Sum(i => i.QuantityOnHand), DashboardService.M(items.Sum(i => i.StockValue)), timeProvider.GetUtcNow(), Money.Currency);
    }

    public async Task<CsvFile> GetInventoryCsvAsync(CancellationToken cancellationToken)
    {
        var report = await GetInventoryAsync(cancellationToken);
        var csv = new CsvWriter();
        csv.Row("Proizvod", "Varijanta", "Šifra (SKU)", "Kategorija", "Stanje", "Minimalno stanje", "Status", "Redovna cena (RSD)", "Vrednost zalihe (RSD)", "Aktivno");
        foreach (var row in report.Items)
        {
            csv.Text(row.ProductName).Text(row.VariantName).Text(row.Sku).Text(row.CategoryName)
                .Number(row.QuantityOnHand).Number(row.LowStockThreshold).Text(row.StatusLabel)
                .Number(row.RegularPrice).Number(row.StockValue).Text(row.IsActive ? "Da" : "Ne").EndRow();
        }

        csv.Text("Ukupno").Text(null).Text(null).Text(null).Number(report.TotalQuantity).Text(null).Text(null).Text(null).Number(report.TotalStockValue).Text(null).EndRow();
        return new CsvFile($"zalihe-{SerbianTime.DateOf(report.GeneratedAt):yyyy-MM-dd}.csv", csv.ToBytes());
    }
}

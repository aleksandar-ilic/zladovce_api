using Microsoft.AspNetCore.Http.HttpResults;
using Zladovce.Api.Auth;
using Zladovce.Application.Analytics;
using Zladovce.Application.Common;

namespace Zladovce.Api.Endpoints;

/// <summary>Dashboard, reports and expenses (Phase 7).</summary>
public static class AnalyticsEndpoints
{
    private const string PeriodHelp =
        "`period`: today, yesterday, last7, last30 (default), thisMonth, lastMonth, thisYear, custom (`from`, `to` as yyyy-MM-dd, " +
        "inclusive, at most 3 years). Days are Europe/Belgrade calendar days (23/25-hour days on DST changes). ";

    public static void MapAdminAnalyticsEndpoints(this IEndpointRouteBuilder admin)
    {
        var dashboard = admin.MapGroup("/dashboard").WithTags("Admin – dashboard");

        dashboard.MapGet("/summary", async ([AsParameters] PeriodQuery query, IDashboardService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.GetSummaryAsync(query.Resolve(clock), ct)))
            .WithName("GetDashboardSummary")
            .WithSummary("P1.8 numbers of the period and of the previous comparable period, plus the current order queues.")
            .WithDescription(
                PeriodHelp +
                "orderedValue: totals of orders placed in the period, current status not Rejected/Cancelled; deliveredValue: totals of " +
                "orders whose Delivered transition is in the period; estimatedCollected = deliveredValue; actualCollected: collected " +
                "amounts by collection date; expenses: expenses dated in the period; estimatedProfit = deliveredValue − expenses; " +
                "averageOrderValue = orderedValue / orderedCount (null without orders); cancelledCount / rejectedCount: transitions in " +
                "the period. `previous`: same numbers for the previous period (same length before it; thisMonth → the previous month " +
                "up to the same day; lastMonth → the month before; thisYear → the previous year up to the same day). `queues` are " +
                "current counts independent of the period: awaitingConfirmation (Received), accepted (Accepted, \"Prihvaćene\"), " +
                "preparing (Preparing, \"U pripremi\"), inPreparation (Accepted + Preparing), awaitingShipment (Preparing), deliveryFailed. " +
                "Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        dashboard.MapGet("/revenue-series", async ([AsParameters] PeriodQuery query, IDashboardService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.GetRevenueSeriesAsync(query.Resolve(clock), ct)))
            .WithName("GetDashboardRevenueSeries")
            .WithSummary("Ordered and delivered value per day (per month for thisYear and ranges over 92 days), zero-filled.")
            .WithDescription(PeriodHelp + "Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        dashboard.MapGet("/order-series", async ([AsParameters] PeriodQuery query, IDashboardService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.GetOrderSeriesAsync(query.Resolve(clock), ct)))
            .WithName("GetDashboardOrderSeries")
            .WithSummary("Orders placed per bucket: orderCount (without Rejected/Cancelled) and placedCount (all).")
            .WithDescription(PeriodHelp + "Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        dashboard.MapGet("/top-products", async ([AsParameters] TopProductsQuery query, IDashboardService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.GetTopProductsAsync(query.Resolve(clock), query.Limit ?? 5, ct)))
            .WithName("GetDashboardTopProducts")
            .WithSummary("Best-selling products of orders delivered in the period: by pieces and by revenue (line totals, no shipping).")
            .WithDescription(PeriodHelp + "`limit` 1–50 (default 5). Names are the order-item snapshots. Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        dashboard.MapGet("/sales-by-category", async ([AsParameters] PeriodQuery query, IDashboardService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.GetSalesByCategoryAsync(query.Resolve(clock), ct)))
            .WithName("GetDashboardSalesByCategory")
            .WithSummary("Revenue and pieces of orders delivered in the period by the product's current category.")
            .WithDescription(PeriodHelp + "A product whose category is archived counts under \"Bez kategorije\" (categoryId null). Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        dashboard.MapGet("/status-distribution", async ([AsParameters] PeriodQuery query, IDashboardService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.GetStatusDistributionAsync(query.Resolve(clock), ct)))
            .WithName("GetDashboardStatusDistribution")
            .WithSummary("Current status of the orders placed in the period (all eight statuses, with Serbian labels).")
            .WithDescription(PeriodHelp + "Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        var reports = admin.MapGroup("/reports").WithTags("Admin – reports");

        reports.MapGet("/sales", async Task<Results<Ok<SalesReportResponse>, FileContentHttpResult>> (
                [AsParameters] SalesReportQuery query, IReportService service, TimeProvider clock, CancellationToken ct) =>
            {
                var period = query.Resolve(clock);
                if (query.Format == "csv")
                {
                    var csv = await service.GetSalesCsvAsync(period, query.GroupBy == "category" ? SalesReportGrouping.Category : SalesReportGrouping.Variant, ct);
                    return TypedResults.File(csv.Content, CsvFile.ContentType, csv.FileName);
                }

                return TypedResults.Ok(await service.GetSalesAsync(period, ct));
            })
            .WithName("GetSalesReport")
            .WithSummary("Sales of orders delivered in the period by product variant and by category. `format=csv` downloads a file.")
            .WithDescription(
                PeriodHelp +
                "CSV (`format=csv`, `groupBy=variant|category`): UTF-8 with BOM, `;` separator, decimal comma, CRLF, RFC 4180 quoting, " +
                "text cells starting with = + - @ get a leading apostrophe; `Content-Type: text/csv; charset=utf-8`, `Content-Disposition: attachment`. " +
                "The documented 200 schema is the JSON shape; with `format=csv` the body is the CSV file. Errors: 400 VALIDATION_FAILED.")
            .Produces<SalesReportResponse>(StatusCodes.Status200OK, "application/json")
            .Problems(StatusCodes.Status400BadRequest);

        reports.MapGet("/inventory", async Task<Results<Ok<InventoryReportResponse>, FileContentHttpResult>> (
                [AsParameters] FormatQuery query, IReportService service, CancellationToken ct) =>
            {
                if (query.Format == "csv")
                {
                    var csv = await service.GetInventoryCsvAsync(ct);
                    return TypedResults.File(csv.Content, CsvFile.ContentType, csv.FileName);
                }

                return TypedResults.Ok(await service.GetInventoryAsync(ct));
            })
            .WithName("GetInventoryReport")
            .WithSummary("Current stock of every variant that is not archived: threshold, status and value at the current regular price.")
            .WithDescription("`format=csv` downloads the same rows as CSV (format as in the sales report). Errors: 400 VALIDATION_FAILED.")
            .Produces<InventoryReportResponse>(StatusCodes.Status200OK, "application/json")
            .Problems(StatusCodes.Status400BadRequest);

        var expenses = admin.MapGroup("/expenses").WithTags("Admin – expenses").RequireAuthorization(AuthPolicies.OwnerOnly);

        expenses.MapGet("/categories", (IExpenseService service) => TypedResults.Ok(service.Categories()))
            .WithName("ListExpenseCategories")
            .WithSummary("The fixed expense categories with Serbian labels (Owner).");

        expenses.MapGet("", async ([AsParameters] ExpenseListQuery query, IExpenseService service, TimeProvider clock, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.ToCriteria(clock), ct)))
            .WithName("ListExpenses")
            .WithSummary("Expenses, newest first, with totals per category (Owner). Optional period filter (same values as the dashboard).")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 403 FORBIDDEN (Staff).")
            .Problems(StatusCodes.Status400BadRequest);

        expenses.MapGet("/{id:guid}", async (Guid id, IExpenseService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetExpense")
            .WithSummary("One expense (Owner).")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        expenses.MapPost("", async Task<Created<ExpenseResponse>> (SaveExpenseRequest request, IExpenseService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/expenses/{created.Id}", created);
            })
            .WithName("CreateExpense")
            .WithSummary("Enter an expense (Owner).")
            .WithDescription("Errors: 400 VALIDATION_FAILED (`expenseDate`, `category`, `description`, `amount`); 403 FORBIDDEN.")
            .Problems(StatusCodes.Status400BadRequest);

        expenses.MapPut("/{id:guid}", async (Guid id, SaveExpenseRequest request, IExpenseService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .WithName("UpdateExpense")
            .WithSummary("Change an expense (Owner).")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        expenses.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, IExpenseService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteExpense")
            .WithSummary("Delete an expense (Owner). The audit log keeps its values.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);
    }
}

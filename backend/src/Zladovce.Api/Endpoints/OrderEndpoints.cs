using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Zladovce.Api.Auth;
using Zladovce.Api.RateLimiting;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Orders;
using Zladovce.Application.Shipping;
using Zladovce.Application.StoreSettings;

namespace Zladovce.Api.Endpoints;

/// <summary>Public checkout (no authentication).</summary>
public static class CheckoutEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static void MapCheckoutEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/orders", async Task<Results<Created<PlacedOrderResponse>, Ok<PlacedOrderResponse>>> (
                CreateOrderRequest request,
                [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
                ICheckoutService service,
                CancellationToken ct) =>
            {
                var key = ParseIdempotencyKey(idempotencyKey);
                var result = await service.PlaceOrderAsync(key, request, ct);
                return result.Created
                    ? TypedResults.Created((string?)null, result.Order)
                    : TypedResults.Ok(result.Order);
            })
            .WithTags("Checkout")
            .AllowAnonymous()
            .RequirePostgresRateLimit(RateLimitPolicies.CreateOrder)
            .WithName("CreateOrder")
            .WithSummary("Place an order (cash on delivery). 201 new order; 200 replay of the same request with the same Idempotency-Key.")
            .WithDescription(
                "Header `Idempotency-Key` (UUID) is required: generate it once when the checkout page opens and send the same key " +
                "with every retry of that submission. Only a successful order uses up the key; after a 409/400 the same key may be " +
                "sent again with the corrected cart. A request with a used key and the same content returns the original order " +
                "(200, `trackingCode` null); with different content 409 IDEMPOTENCY_KEY_REUSED. Prices, shipping and totals are " +
                "computed from the database with the rules of the cart quote; stock is deducted at once. `trackingCode` is always " +
                "null until tracking codes are introduced (Phase 6). " +
                "Errors: 400 VALIDATION_FAILED (field paths such as `customer.phone`, `address.postalCode`, `items[0].quantity`, " +
                "`acceptedTerms`, `idempotencyKey`), BAD_REQUEST (malformed JSON); 409 with the fresh quote in `quote`: CART_CHANGED " +
                "(a line unavailable or reduced, `knownUnitPrice` or `expectedTotal` differs; `errors` names the lines), " +
                "INSUFFICIENT_STOCK (stock taken meanwhile; `errors[\"items[i].quantity\"]`), MINIMUM_ORDER_NOT_MET, SHIPPING_DISABLED; " +
                "409 IDEMPOTENCY_KEY_REUSED; 429 RATE_LIMITED (5 per 10 minutes per IP, `Retry-After` header in seconds).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);
    }

    internal static Guid ParseIdempotencyKey(string? value) =>
        Guid.TryParse(value?.Trim(), out var key) && key != Guid.Empty
            ? key
            : throw new RequestValidationException("idempotencyKey", "Zaglavlje Idempotency-Key je obavezno i mora biti UUID.");
}

/// <summary>Order administration (Owner and Staff).</summary>
public static class AdminOrderEndpoints
{
    public static void MapAdminOrderEndpoints(this IEndpointRouteBuilder admin)
    {
        var orders = admin.MapGroup("/orders").WithTags("Admin – orders");

        orders.MapGet("", async ([AsParameters] AdminOrderListQuery query, IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.ToCriteria(), ct)))
            .WithName("ListAdminOrders")
            .WithSummary("Orders with search, status and date filters, sorting and paging.")
            .WithDescription(
                "`q`: order number, customer name (ignores case and diacritics), email, phone. `status`: several values comma-separated " +
                "and/or repeated. `from`/`to`: Serbian local dates (yyyy-MM-dd), inclusive. `sort`: newest (default), oldest, totalDesc, " +
                "totalAsc. Errors: 400 VALIDATION_FAILED, BAD_REQUEST (malformed date).")
            .Problems(StatusCodes.Status400BadRequest);

        orders.MapGet("/summary", async (IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetSummaryAsync(ct)))
            .WithName("GetAdminOrderSummary")
            .WithSummary("Number of orders per status (all eight, zero included), e.g. for the menu badge (Received).");

        orders.MapGet("/{id:guid}", async (Guid id, IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetAdminOrder")
            .WithSummary("Order detail: contact, address, items, totals, payment, timeline, notes, shipments and the allowed next statuses.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        orders.MapPost("/{id:guid}/status", async (Guid id, ChangeOrderStatusRequest request, IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ChangeStatusAsync(id, request, ct)))
            .WithName("ChangeAdminOrderStatus")
            .WithSummary("Move the order to the next status (P1.5). Returns the updated order.")
            .WithDescription(
                "Allowed: Received → Accepted | Rejected | Cancelled; Accepted → Preparing | Cancelled; Preparing → Shipped | Cancelled; " +
                "Shipped → Delivered | DeliveryFailed; DeliveryFailed → Shipped | Cancelled; Delivered, Cancelled, Rejected are final. " +
                "Shipped requires `shipment` (active courier; tracking number, date (default now), note optional). Stock returns " +
                "automatically on Rejected and on Cancelled before shipping; cancelling after DeliveryFailed requires `restock` " +
                "(true = goods back on stock). History, shipment, stock movements and audit are one transaction. " +
                "Errors: 400 VALIDATION_FAILED (`shipment`, `shipment.courierCompanyId`, `shipment.shippedAt`, `restock`); 404 NOT_FOUND; " +
                "409 INVALID_STATUS_TRANSITION (with `allowedNextStatuses`), CONCURRENT_EDIT (stale `version`).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        orders.MapPost("/{id:guid}/notes", async (Guid id, AddOrderNoteRequest request, IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.AddNoteAsync(id, request, ct)))
            .WithName("AddAdminOrderNote")
            .WithSummary("Add an internal note without changing the status. Returns the updated order (`notes`).")
            .WithDescription("Does not change `version`. Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        orders.MapPut("/{id:guid}/shipments/{shipmentId:guid}", async (
                Guid id, Guid shipmentId, UpdateShipmentRequest request, IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateShipmentAsync(id, shipmentId, request, ct)))
            .WithName("UpdateAdminOrderShipment")
            .WithSummary("Correct a shipment: courier, tracking number, date, note. Returns the updated order.")
            .WithDescription(
                "Every member must be sent. The courier must be active unless it is the shipment's current one. Does not change " +
                "the order `version`. Errors: 400 VALIDATION_FAILED (`courierCompanyId`, `shippedAt`); 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        orders.MapPut("/{id:guid}/payment", async (Guid id, SetOrderPaymentRequest request, IOrderAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SetPaymentAsync(id, request, ct)))
            .WithName("SetAdminOrderPayment")
            .WithSummary("Record the amount actually collected from the courier and when, or clear it (`collectedAmount` null).")
            .WithDescription(
                "Delivered orders only. Errors: 400 VALIDATION_FAILED (`collectedAmount`, `collectedAt`); 404 NOT_FOUND; " +
                "409 ORDER_NOT_DELIVERED, CONCURRENT_EDIT.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }
}

/// <summary>Courier companies and store settings.</summary>
public static class AdminShippingEndpoints
{
    public static void MapAdminShippingEndpoints(this IEndpointRouteBuilder admin)
    {
        var couriers = admin.MapGroup("/couriers").WithTags("Admin – couriers");

        couriers.MapGet("", async ([FromQuery(Name = "activeOnly")] bool? activeOnly, ICourierService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(includeInactive: activeOnly != true, ct)))
            .WithName("ListAdminCouriers")
            .WithSummary("Courier companies by sort order and name. `activeOnly=true` for the shipment form; default lists all.");

        couriers.MapGet("/{id:guid}", async (Guid id, ICourierService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetAdminCourier")
            .WithSummary("A courier company.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        couriers.MapPost("", async Task<Created<CourierResponse>> (SaveCourierRequest request, ICourierService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/couriers/{created.Id}", created);
            })
            .RequireAuthorization(AuthPolicies.OwnerOnly)
            .WithName("CreateAdminCourier")
            .WithSummary("Add a courier company (Owner).")
            .WithDescription("Errors: 400 VALIDATION_FAILED (`name`, `trackingUrlTemplate`); 403 FORBIDDEN (Staff); 409 COURIER_NAME_TAKEN (`errors.name`).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        couriers.MapPut("/{id:guid}", async (Guid id, SaveCourierRequest request, ICourierService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .RequireAuthorization(AuthPolicies.OwnerOnly)
            .WithName("UpdateAdminCourier")
            .WithSummary("Change a courier company (Owner). Deactivate with `isActive: false`; couriers are never deleted.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 403 FORBIDDEN (Staff); 404 NOT_FOUND; 409 COURIER_NAME_TAKEN (`errors.name`).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        var settings = admin.MapGroup("/settings").WithTags("Admin – settings").RequireAuthorization(AuthPolicies.OwnerOnly);

        settings.MapGet("", async (IStoreSettingsService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAdminAsync(ct)))
            .WithName("GetAdminSettings")
            .WithSummary("All store settings (Owner).")
            .WithDescription("Errors: 403 FORBIDDEN (Staff).");

        settings.MapPut("", async (UpdateStoreSettingsRequest request, IStoreSettingsService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(request, ct)))
            .WithName("UpdateAdminSettings")
            .WithSummary("Replace the store settings (Owner). Every member must be sent; `version` as last read.")
            .WithDescription(
                "The logo is not changed here. Shipping changes apply to the next quote and order at once. " +
                "Errors: 400 VALIDATION_FAILED; 403 FORBIDDEN (Staff); 409 CONCURRENT_EDIT.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
    }
}

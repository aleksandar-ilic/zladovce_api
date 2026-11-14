# Zladovce – backend

.NET 10 modular monolith: ASP.NET Core minimal API (runs on Kestrel locally and on AWS Lambda behind
API Gateway HTTP API), EF Core + PostgreSQL 17. Design documents are in [`../docs`](../docs).

Phase 2 (Foundation) contains: solution structure, the first migration (`admin_users`, `store_settings`,
`audit_logs`), error handling, logging, OpenAPI, authentication/authorization, health endpoints,
`GET /api/v1/store`, `GET /api/v1/admin/me` and the migration Lambda.

Phase 3 (Catalog and administration) adds: migration `CatalogAndInventory` (`categories`, `products`,
`product_variants`, `product_images`, `inventory`, `inventory_movements`), category/product/variant/image
administration, the public catalog, inventory (adjustments, thresholds, movement history, low stock, and
`IInventoryService` for checkout), media uploads (pre-signed POST, image processing to WebP renditions, the image
processor Lambda), MinIO for local development, and the database role bootstrap of the migration Lambda.

Phase 4 (Storefront, backend part) adds the public cart quote (`POST /api/v1/cart/quote`, `ICartPricingService`, which
Phase 5 checkout reuses) and the SEO routes for prerendering and the sitemap (`GET /api/v1/seo/routes`). No migration.

Phase 5 (Checkout and orders, backend part) adds migration `CheckoutAndOrders`, idempotent checkout (`POST /api/v1/orders`),
the order status state machine with stock returns, order administration (list, detail, status, notes, shipments, collected
payment, per-status summary), couriers, store settings administration, bestsellers, `limitReason` in the cart quote and a
PostgreSQL-backed rate limiter.

Phase 6 (Tracking and notifications, backend part) adds migration `TrackingAndNotifications` (`order_tracking_tokens`,
`email_outbox`, `email_logs`), tracking codes issued by checkout and by every status email, the public tracking lookup, the
transactional email outbox with its dispatcher (outbox worker Lambda, or a background service locally), SES and SMTP
senders, the six Serbian order emails, admin email visibility / retry / resend confirmation / revoke tracking codes, and
Mailpit for local development. See "Tracking and emails (Phase 6)".

## Layout

```
backend/
├── Zladovce.sln
├── Directory.Build.props        net10.0, nullable, warnings as errors
├── Directory.Packages.props     central package versions
├── docker-compose.yml           local PostgreSQL 17 (host port 5433), MinIO (9000/9001) and Mailpit (1025/8025)
├── dotnet-tools.json            local tool: dotnet-ef
├── openapi/zladovce-v1.json     committed OpenAPI document (source for frontend models)
├── scripts/generate-openapi.ps1
├── src/
│   ├── Zladovce.Domain/          entities and rules, no dependencies
│   ├── Zladovce.Application/     services, DTOs, validators, ports
│   ├── Zladovce.Infrastructure/  EF Core, migrations, database connection, Cognito profile lookup
│   ├── Zladovce.Api/             endpoints, auth, error handling, logging, OpenAPI, Lambda HTTP entry
│   └── Zladovce.Lambda/          non-HTTP handlers (MigrationRunner, ImageProcessorFunction, OutboxWorkerFunction)
└── tests/
    ├── Zladovce.UnitTests/
    └── Zladovce.IntegrationTests/  real PostgreSQL through Testcontainers
```

Dependency rule: `Domain` ← `Application` ← `Infrastructure` ← `Api` / `Lambda`. Each project is organised by
module folder (`Identity`, `StoreSettings`, `Audit`, `Categories`, `Catalog`, `Inventory`, `Media`, `Cart`, `Seo`, `Orders`, `Shipping`, `Customers`, `Tracking`, `Notifications`, `Common`).

## Prerequisites

- .NET SDK that can build `net10.0` (.NET 10 SDK or newer) and the .NET 10 runtime
- Docker (local database and integration tests)

## Start PostgreSQL, MinIO and Mailpit

```powershell
cd backend
docker compose up -d        # PostgreSQL 17 on localhost:5433, db/user "zladovce", password "zladovce_dev"
                            # MinIO (S3-compatible) API http://localhost:9000, console http://localhost:9001
                            # Mailpit SMTP localhost:1025, web UI with every sent email http://localhost:8025
docker compose down         # stop (add -v to delete the data volumes)
```

These credentials are for local development only.

MinIO stands in for the AWS media bucket. The compose file starts the server (user `zladovce`, password
`zladovce_dev_minio`), creates the bucket `zladovce-media-dev` and makes `media/*` publicly readable (like
CloudFront on AWS); `uploads/*` stays private. Browser uploads are allowed from `http://localhost:4201` (the admin
application) through `MINIO_API_CORS_ALLOW_ORIGIN` (MinIO has no per-bucket CORS). The images are Chainguard builds
of the open-source MinIO server and client, because MinIO no longer publishes its own container images.

Mailpit (`axllent/mailpit:v1.32.0`, MIT) catches every email the API sends in Development; nothing reaches real recipients.
Open http://localhost:8025 to read them (HTML, text part, headers). Messages are kept in the `mailpitdata` volume. While the API
runs in Development, its background dispatcher sends queued emails every 5 seconds.

## Run the API

```powershell
cd backend
dotnet run --project src/Zladovce.Api
```

- URL: `http://localhost:5080`
- Swagger UI: `http://localhost:5080/swagger` (Development only), document at `/openapi/v1.json`
- In Development `Database:MigrateOnStartup` is `true` (see `appsettings.Development.json`), so the schema is
  created on first start.

Quick check:

```powershell
curl http://localhost:5080/health
curl http://localhost:5080/health/ready
curl http://localhost:5080/api/v1/store
curl http://localhost:5080/api/v1/admin/me -H "X-Dev-User-Email: owner@example.com" -H "X-Dev-User-Role: Owner"
```

## Endpoints (Phase 2)

| Method | Path | Auth | Response |
|---|---|---|---|
| GET | `/health` | – | `{ "status": "ok" }` – liveness, no database access |
| GET | `/health/ready` | – | `{ "status": "ready" }`, or `503` problem when the database is unreachable |
| GET | `/api/v1/store` | – | Public store settings |
| GET | `/api/v1/admin/me` | `AdminAny` | `{ id, email, fullName, role }` (`role` is `Owner` or `Staff`) |

Errors are RFC 9457 Problem Details (`application/problem+json`) with a stable `code`, Serbian `title` and
`detail`, `traceId`, and an `errors` map (camelCase field path → messages) for validation failures and for the
conflicts caused by one field (`409 SLUG_TAKEN` → `slug`; `409 SKU_TAKEN` → `sku` for the product base SKU and for a
single variant add/update, `variants[i].sku` for each taken variant SKU of a create-product request, `i` = index in
the request):

```json
{
  "type": "https://zladovce.com/errors/validation-failed",
  "title": "Neispravni podaci",
  "status": 400,
  "code": "VALIDATION_FAILED",
  "detail": "Proverite unete podatke i pokušajte ponovo.",
  "errors": { "email": ["..."] },
  "traceId": "..."
}
```

Generic codes: `VALIDATION_FAILED`, `BAD_REQUEST`, `UNAUTHORIZED`, `FORBIDDEN`, `ADMIN_INACTIVE`,
`ADMIN_NOT_PROVISIONED`, `NOT_FOUND`, `METHOD_NOT_ALLOWED`, `CONFLICT`, `CONCURRENT_EDIT`, `RATE_LIMITED`,
`INTERNAL_ERROR`, `SERVICE_UNAVAILABLE` (Phase 5 codes: see "Endpoints (Phase 5)"). Checkout conflicts add `quote`
(the fresh cart quote) and `INVALID_STATUS_TRANSITION` adds `allowedNextStatuses` to the problem body. Application code throws `RequestValidationException`,
`NotFoundException`, `ConflictException` or `ForbiddenException` (`Zladovce.Application.Common.Errors`);
one middleware turns them into responses.

Every response carries `X-Correlation-Id` (the caller's value when it is well formed, otherwise generated);
the same id is on every log line of the request. Logs are JSON on stdout. Request logs contain the path
only, never the query string, bodies, emails or tokens.

Request DTOs are validated automatically: any endpoint argument under `/api/v1` whose type has a registered
FluentValidation `IValidator<T>` is validated before the handler runs. Put validators in
`Zladovce.Application`; they are registered by assembly scan.

Required members ("send every field"): a request member declared with the C# `required` keyword must be present in
the JSON body; a nullable one may be `null`, but it must be there. A missing member is a `400 VALIDATION_FAILED` with
the field path in `errors` (`"isFeatured"`, `"variants[1].isActive"`, message "Polje je obavezno …"), never a silent
default. `RequiredMembers` (a System.Text.Json contract modifier plus a check in the validation filter) implements
this; the OpenAPI document lists the same members as `required`, and a nullable member whose validator has a
`NotNull`/`NotEmpty` rule is documented as non-nullable (sending `null` still gives `VALIDATION_FAILED`).
Use `required` init properties (not positional records) for request bodies that need this.

| Body | Must be sent |
|---|---|
| `POST products` | `categoryId`, `name`, `sku`, `variants` (each a full variant, below). Optional: `slug`, `shortDescription`, `description`, `isActive` (default true), `isFeatured` (default false), `seoTitle`, `seoDescription` |
| `PUT products/{id}` | every member: `version`, `categoryId`, `name`, `slug`, `shortDescription`, `description`, `sku`, `isActive`, `isFeatured`, `seoTitle`, `seoDescription` |
| variant (`POST`/`PUT …/variants`, items of `POST products`) | `name`, `sku`, `unitAmount`, `measurementUnit`, `regularPrice`, `discountPrice` (may be null), `isActive`; on create also `initialStock`, `lowStockThreshold`. Optional: `sortOrder` |
| `POST`/`PUT categories` | every member: `name`, `slug`, `description`, `imageKey`, `sortOrder`, `isActive`, `seoTitle`, `seoDescription` |
| `PUT products/{id}/images/order` | `imageIds`, `mainImageId` (may be null) |
| `PUT products/{id}/images/{imageId}` | `altText` (may be null) |
| `POST products/{id}/images` | `uploadKey`. Optional: `altText` |
| `PUT inventory/{variantId}/threshold` | `lowStockThreshold` |
| `POST inventory/{variantId}/adjustments` | `quantityDelta`, `reason` |
| `PUT categories/order` | `ids` |
| `POST media/upload-url` | `fileName`, `contentType`, `size`, `purpose` |

## Endpoints (Phase 3)

Public (no authentication) under `/api/v1/catalog`: `GET categories`, `GET categories/{slug}`, `GET products`
(`category`, `q`, `minPrice`, `maxPrice`, `inStock`, `featured`, `sort` = `newest|priceAsc|priceDesc|name|bestselling`,
`page`, `pageSize`), `GET products/{slug}`, `GET products/{slug}/related?limit=`, `GET availability?variantIds=`.

Admin (`AdminAny`, i.e. Owner or Staff) under `/api/v1/admin`: `categories` (list, create, read, update, archive,
`PUT categories/order`), `products` (list, create, read, update with `version`, `POST {id}/archive`,
`POST {id}/restore`), `products/{id}/variants[/{variantId}]`, `products/{id}/images[/order|/{imageId}]`,
`POST media/upload-url`, `inventory` (list with `q`, `status`, `sort`, paging; `POST {variantId}/adjustments`,
`PUT {variantId}/threshold`, `GET {variantId}/movements`), `GET dashboard/low-stock`.

The exact contract (fields, query parameters, status codes, error codes in each operation's description) is
`openapi/zladovce-v1.json`.

Catalog rules:

- Prices exist on variants only (`numeric(12,2)`, RSD). The effective price is the discount price when set, else the
  regular price (`Pricing` in the domain). A product's listing price is the lowest effective price of its active
  variants; `hasPriceRange` tells the shop to show "od …".
- The shop shows a product only when it is active, not archived, in an active (and not archived) category and has
  at least one active variant. Exact stock is never published: variants expose `inStock` and
  `maxOrderQuantity = min(stock, Catalog:MaxOrderQuantity)` (default 20).
- Sort `bestselling` orders by pieces sold in delivered orders (Phase 5, see "Bestsellers").
- Search (`q`) ignores case and Serbian diacritics ("dzem" finds "Džem"): `lower(zladovce_unaccent(...)) LIKE`,
  where `zladovce_unaccent` is an IMMUTABLE wrapper around `unaccent` so that `products.name` can have a trigram index.
- Slugs are generated from the name when not given (č, ć → c, š → s, ž → z, đ → dj) and get `-2`, `-3`, … when
  taken; an explicitly requested slug that is taken gives `409 SLUG_TAKEN`. Slugs and SKUs are unique among records
  that are not archived (partial unique indexes), so archiving frees them.
- Products use optimistic concurrency: `version` (PostgreSQL `xmin`) must be sent back on update, a stale value
  gives `409 CONCURRENT_EDIT`.

Inventory:

- Every stock change is a conditional `UPDATE … WHERE quantity_on_hand + delta >= 0` plus an
  `inventory_movements` row with `quantity_after`, in one transaction; `quantity_on_hand >= 0` is also a check
  constraint. Manual adjustments require a reason (also a check constraint) and write an `Inventory.Adjusted` audit row.
- `IInventoryService.DeductAsync` / `ReturnAsync` take a list of variant + quantity, process variants in id order (no
  deadlocks), are all-or-nothing (`InsufficientStockException` lists every shortage) and join the caller's
  transaction when there is one (Phase 5 checkout). Parallel deductions are integration-tested against oversell.
- Status of a variant: `out` (nothing on stock), `low` (at or below its threshold), `ok` (above it). The inventory list
  filter `status` accepts several values, comma-separated and/or repeated (`status=low,out`, `status=low&status=out`)
  and returns rows in any of them; one value works as before. "Low stock" on the dashboard (`GET dashboard/low-stock`)
  is exactly `low` + `out` and uses the same filter, so **the dashboard card should link to the inventory list with
  `status=low,out`** – its `totalItems` equals the card's `totalItems`.
- `inventory_movements.order_id` references `orders` (foreign key added in Phase 5).

Audit: an EF Core `SaveChanges` interceptor writes one `audit_logs` row per created/updated/archived/restored/
deleted category, product, variant, product image and inventory record (`{Entity}.{Created|Updated|Archived|Restored|Deleted}`,
changed fields as `{ "field": { "old": …, "new": … } }`, administrator id, IP), in the same transaction. Stock
quantity changes bypass the change tracker and are audited explicitly (`Inventory.Adjusted`).

## Endpoints (Phase 4)

Public (no authentication):

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/v1/cart/quote` | Prices the browser cart from the database |
| GET | `/api/v1/seo/routes` | `{ categories: [{ slug, updatedAt }], products: [{ slug, updatedAt }] }` of visible items, sorted by slug |

### Cart quote

Request (`items` required, 1–50 lines; per line `variantId` and `quantity` required, `knownUnitPrice` optional):

```json
{ "items": [ { "variantId": "0193…", "quantity": 2, "knownUnitPrice": 590.00 } ] }
```

- Quantity 1–99 per line. Lines with the same variant are merged (quantities added, position of the first line, first
  `knownUnitPrice` that was sent). The client never sends prices that are used: everything is priced from the database.
- `knownUnitPrice` is the `unitPrice` of the previous quote that the browser cart stored. When the current price differs,
  the quote contains a `priceChanged` adjustment with `previousUnitPrice` and `unitPrice`. Omit it (or send `null`) and
  price changes are not reported.
- A line is **sellable** when the variant is active and not archived, the product is active and not archived, its category
  is active and not archived, and `maxOrderQuantity = min(stock, Catalog:MaxOrderQuantity) > 0` (the catalog rule).

Response:

| Member | Meaning |
|---|---|
| `lines[]` | `{ variantId, productId, productName, productSlug, variantName, sku, unitAmount, measurementUnit, image (catalog image shape, main image) \| null, unitPrice, regularPrice, discountPrice \| null, requestedQuantity, quantity, lineTotal, maxOrderQuantity }`; `quantity = min(requestedQuantity, maxOrderQuantity)`, `lineTotal = unitPrice × quantity`. Request order |
| `adjustments[]` | `{ variantId, reason, requestedQuantity, availableQuantity, previousUnitPrice \| null, unitPrice \| null }`, request order. `unavailable`: removed from `lines`, `availableQuantity` 0. `quantityReduced`: capped, `availableQuantity` = the line quantity. `priceChanged`: both prices set. One variant can have `quantityReduced` and `priceChanged` |
| `subtotal` | Sum of `lineTotal` |
| `shipping` | `{ enabled, price (charged for this cart), basePrice (configured), freeShippingThreshold \| null, isFree, amountUntilFreeShipping \| null }` |
| `minOrderAmount`, `meetsMinimumOrder`, `amountUntilMinimumOrder` | Minimum order, compared with the subtotal |
| `total` | `subtotal + shipping.price` |
| `canCheckout` | `false` when shipping is disabled, there are no lines or the minimum is not met |
| `currency` | `"RSD"` |

Rules (`CartTotals` in the domain, the single implementation): money rounded to two decimals; shipping is charged
unless `subtotal >= freeShippingThreshold`; nothing is charged for an empty cart or while shipping is disabled;
`amountUntilFreeShipping` is set only when shipping is enabled, costs something and the threshold is not reached;
the minimum order compares the subtotal (without shipping). The exact stock is never returned.

Errors: `400 VALIDATION_FAILED` with field paths (`items`, `items[i].quantity`, `items[i].variantId`,
`items[i].knownUnitPrice`); `400 BAD_REQUEST` for malformed JSON or a value of the wrong JSON type (a malformed uuid,
a quantity sent as a string), as everywhere else in the API.

`ICartPricingService.QuoteAsync(items)` (Application, no HTTP types) does one query for all requested variants (product,
category check, main image, stock) plus one for the store settings, and joins the caller's transaction. Phase 5
checkout calls it inside the order transaction and then deducts stock with `IInventoryService`. Not rate limited
(API Gateway throttling only).

CORS: the storefront origins (`http://localhost:4200` and `http://localhost:4210` in development) is in `Cors:AllowedOrigins`; the policy allows
`POST` and the `Content-Type` header, so the JSON preflight succeeds (integration-tested).

### SEO routes

Visibility is the public catalog's (`CatalogVisibility`): active, not archived categories (also empty ones, whose
page exists) and visible products. `updatedAt` is the category's/product's `updated_at` (product edits, variant and
image changes touch it; stock changes do not). Blog posts and blog categories are added in Phase 7 as new members of
the same response (`{ slug, updatedAt }`); there are no placeholder entries.

## Endpoints (Phase 5 – checkout and orders)

Migration `CheckoutAndOrders`: `customers`, `addresses` (immutable), `orders`, `order_items`, `order_status_history`
(append-only), `order_notes` (append-only), `order_number_counters`, `courier_companies`, `shipments`, `rate_limit_buckets`,
and the foreign key `inventory_movements.order_id → orders` deferred by Phase 3. Indexes as in DATABASE.md
(`orders (status, placed_at desc)`, `(placed_at)`, `(contact_email)`, `(contact_phone)`, unique `order_number` and
`idempotency_key`, `order_status_history (order_id, created_at)` and `(new_status, created_at)` for "delivered in a period").
Check constraints: `total = subtotal + shipping_price`, `line_total = unit_price * quantity`, status lists, order-number
format, postal code `^[0-9]{5}$`, collected amount and date both set or both null, tracking URL template https with
`{trackingNumber}`. `orders.request_hash` (bytea, SHA-256) is an addition to DATABASE.md, needed for idempotency.

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/orders` | – (rate limited) | Checkout. Header `Idempotency-Key: <uuid>` required |
| GET | `/api/v1/catalog/bestsellers?limit=` | – | Best sellers from Delivered orders (1–24, default 8) |
| GET | `/api/v1/admin/orders` | AdminAny | `q`, `status` (multi), `from`, `to` (Serbian dates), `sort` = `newest`, `oldest`, `totalDesc`, `totalAsc`, paging |
| GET | `/api/v1/admin/orders/summary` | AdminAny | Count per status (all eight) and total |
| GET | `/api/v1/admin/orders/{id}` | AdminAny | Detail with timeline, notes, shipments, payment, `allowedNextStatuses` |
| POST | `/api/v1/admin/orders/{id}/status` | AdminAny | `newStatus`, `version`, `internalNote?`, `customerMessage?`, `shipment?`, `restock?` |
| POST | `/api/v1/admin/orders/{id}/notes` | AdminAny | `note` (internal, no status change) |
| PUT | `/api/v1/admin/orders/{id}/shipments/{shipmentId}` | AdminAny | `courierCompanyId`, `trackingNumber`, `shippedAt`, `note` (all sent) |
| PUT | `/api/v1/admin/orders/{id}/payment` | AdminAny | `collectedAmount` (null clears), `collectedAt` (null = now), `version`; Delivered only |
| GET | `/api/v1/admin/couriers[?activeOnly=true]`, `/{id}` | AdminAny | Courier companies |
| POST, PUT | `/api/v1/admin/couriers`, `/{id}` | OwnerOnly | Create, change, deactivate (`isActive: false`); never deleted, none seeded |
| GET, PUT | `/api/v1/admin/settings` | OwnerOnly | Every store setting except the logo; `version` (xmin) |

### Checkout

Body: `items[{ variantId, quantity, knownUnitPrice? }]` (1–50 lines, 1–99 each, duplicates merged), `customer{ firstName,
lastName, email, phone }`, `address{ city, street, postalCode }`, `deliveryNote?` (≤ 500), `acceptedTerms` (must be true),
`expectedTotal?`. Names ≤ 100 (letters, space, `-`, `.`, `'`), email ≤ 254, phone a Serbian number (`+381…`, `00381…`,
`0…`; stored in E.164 `+381641234567`), city ≤ 100, street ≤ 200, postal code 5 digits. Messages are Serbian.

One transaction (`CheckoutService`): advisory lock on the key → idempotency lookup → `ICartPricingService.QuoteAsync`
(the quote's rules) → refusal checks → customer upsert by email (`ON CONFLICT`, latest name/phone) → order number (yearly
counter row, `INSERT … ON CONFLICT DO UPDATE … RETURNING`, locked until commit, year in Europe/Belgrade) → address, order,
item snapshots, history `Received` (no admin) → `IInventoryService.DeductAsync` (conditional updates in variant-id order,
movements `OrderPlaced` with `order_id`) → commit. Response `201 { orderNumber, placedAt, items[{ variantId, productName,
variantName, sku, unitPrice, quantity, lineTotal }], subtotal, shippingPrice, total, currency, paymentMethod: "CashOnDelivery",
trackingCode }`. `trackingCode` (Phase 6) is issued in the same transaction as the order, together with the confirmation outbox
row; it is returned only in the 201 response (`null` on a replay, because the plain code is never stored).

Idempotency rules:

- The browser creates one UUID when checkout opens and sends it with every retry of that submission.
- Only a committed order uses the key. After any 400/409/429 nothing is stored and the same key may be sent again.
- Same key + same content → `200` with the original order (`trackingCode` null). Same key + different content →
  `409 IDEMPOTENCY_KEY_REUSED`. "Same content" = `orders.request_hash`, SHA-256 of the normalised request: merged and
  sorted lines, trimmed texts, lower-case email, E.164 phone, note; `knownUnitPrice` and `expectedTotal` are not part of it.
- Concurrent requests with one key are serialised by `pg_advisory_xact_lock`; the unique index is the last guard.

Refusals (409, body has `quote` = the fresh cart quote, nothing stored): `SHIPPING_DISABLED`; `CART_CHANGED` (an adjustment
in the fresh quote – unavailable, quantity reduced, price differs from `knownUnitPrice` – or `expectedTotal` differs;
`errors` keys `items[i].variantId`, `items[i].quantity`, `items[i].knownUnitPrice`, `expectedTotal`, `i` = request index);
`MINIMUM_ORDER_NOT_MET`; `INSUFFICIENT_STOCK` (stock taken between pricing and deduction; `errors["items[i].quantity"]`
"Dostupno najviše: N."). Client prices are never used.

Rate limit: `POST /orders` 5 per 10 minutes per client IP (`RateLimiting:Policies:CreateOrder`), PostgreSQL table
`rate_limit_buckets` (fixed windows, key = policy + SHA-256 of the IP, one atomic upsert on its own connection, so all
Lambda instances share it). Every request counts, including invalid ones. Exceeded → `429 RATE_LIMITED` with
`Retry-After` (seconds; exposed through CORS). Reusable: `.RequirePostgresRateLimit(policyName)` on any endpoint plus a
policy in `RateLimitPolicies`. Client IP: the `X-Forwarded-For` entry `ClientIp:TrustedProxyHops` positions from the right
(default 1 = the address API Gateway appended; entries further left are client-supplied), else the connection address.
`IRateLimiter.PurgeExpiredAsync` deletes old buckets; the outbox worker calls it on every run (windows older than one day). `RateLimiting:Enabled=false`
switches limits off (integration tests).

### Status state machine (P1.5) and stock (P1.6)

| From | Allowed next | Needs | Stock |
|---|---|---|---|
| Received (Primljena) | Accepted, Rejected, Cancelled | – | Rejected/Cancelled: returned automatically (`OrderCancelled`) |
| Accepted (Prihvaćena) | Preparing, Cancelled | – | Cancelled: returned automatically |
| Preparing (U pripremi) | Shipped, Cancelled | Shipped: `shipment.courierCompanyId` (active); `trackingNumber`, `shippedAt` (default now), `note` optional | Cancelled: returned automatically |
| Shipped (Poslata) | Delivered, DeliveryFailed | – | none |
| DeliveryFailed (Neuspešna isporuka) | Shipped (new shipment row), Cancelled | Cancelled: `restock` true/false required | `restock: true` → `ReturnRestock`; false → nothing |
| Delivered (Isporučena), Cancelled (Otkazana), Rejected (Odbijena) | – (final) | | |

`OrderStatusMachine` (Domain) is the only definition; the detail returns it as `allowedNextStatuses[{ status,
requiresShipment, stockReturn: None|Automatic|AdminChoice, requiresRestockChoice }]`. A transition is one transaction:
version check (`xmin`; stale → `409 CONCURRENT_EDIT`), history row (previous, new, admin, internal note, customer message),
shipment, stock return, `Order.StatusChanged` audit. Invalid → `409 INVALID_STATUS_TRANSITION` with `allowedNextStatuses`.
`shipment` outside Shipped or `restock` outside DeliveryFailed → Cancelled → `400` (`errors.shipment` / `errors.restock`).
The same transaction queues the status email (Phase 6, see "Tracking and emails").

Notes are a separate append-only table `order_notes` (not history rows with `previous = new`): the history stays exactly one
row per transition, so the tracking timeline and the "delivered in period" metric need no filter. Shipment edits and
notes do not change the order `version`; status and payment changes do. Shipment dates and collection dates must not be in
the future (5 min tolerance) or before the order was placed. Tracking URL = template with the URL-escaped tracking number.

Audit: `Order.StatusChanged`, `Order.NoteAdded`, `Order.PaymentRecorded`, `Order.PaymentCleared` explicitly;
`CourierCompany.*`, `Shipment.*`, `StoreSettings.Updated` through the interceptor.

### Bestsellers

`GET /catalog/bestsellers` and `sort=bestselling` use one metric: pieces of order items whose order is Delivered and whose
Delivered transition is within `Catalog:BestsellerWindowDays` (default 90), visible products only; ties newest first.
Bestsellers lists only products with such sales (empty without delivered orders); the sorted product list shows the others
after them, newest first.

### Cart quote addition

`adjustments[].limitReason`: `stock` (stock below the per-order cap) or `orderLimit` (the cap `Catalog:MaxOrderQuantity`
applies) on `quantityReduced`, null otherwise.

New error codes: `IDEMPOTENCY_KEY_REUSED`, `CART_CHANGED`, `MINIMUM_ORDER_NOT_MET`, `SHIPPING_DISABLED`,
`INVALID_STATUS_TRANSITION`, `ORDER_NOT_DELIVERED`, `COURIER_NAME_TAKEN` (`errors.name`); `INSUFFICIENT_STOCK` and
`RATE_LIMITED` are now returned by checkout.

## Tracking and emails (Phase 6)

### Tracking codes

- 130 bits from `RandomNumberGenerator`, 26 Crockford Base32 characters (`0-9 A-Z` without `I L O U`), displayed as
  `XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX` (`TrackingCode` in the domain).
- Input normalisation: hyphens and whitespace are removed, case is ignored, `O → 0`, `I`/`L → 1`; anything that is then not
  exactly 26 alphabet characters (or is longer than 64 characters raw) is malformed.
- Only `SHA-256(canonical code)` is stored (`order_tracking_tokens.token_hash`, unique). Malformed input is still hashed (with a
  prefix, so it can never match) and looked up, so it costs the same as an unknown code.
- Issued: one by checkout (returned once as `trackingCode` in the 201 response and sent in the confirmation), a fresh one per
  status email (links work without storing plain codes), one per "resend confirmation" and, when needed, one by an admin retry.
  All codes of an order stay valid until an administrator revokes them (`POST …/tracking/revoke` revokes every code issued so
  far; codes issued later work).
- Email links: `{Storefront:BaseUrl}/track-order#kod={grouped code}` – the code is in the URL fragment, so it never reaches
  server or CDN logs; the page sends it in the POST body.

### Public lookup

`POST /api/v1/tracking/lookup` `{ "code": "…" }` (member required, `null` allowed) →
`{ orderNumber, status, statusLabel, placedAt, timeline: [{ status, label, at, customerMessage }], shipment: { courierName,
trackingNumber, trackingUrl, shippedAt } | null }`. `timeline` is the real `order_status_history`, oldest first; `label` is the
customer wording ("Porudžbina primljena", "Porudžbina prihvaćena", "Proizvodi se pripremaju", "Pošiljka predata kurirskoj
službi", "Porudžbina isporučena", "Porudžbina otkazana", "Porudžbina nije prihvaćena", "Isporuka nije uspela"); `shipment` is the
latest shipment. No name, address, phone, email, items, prices, internal notes or admin names. A hit updates `last_used_at`.
Errors: `404 TRACKING_NOT_FOUND` with one identical body for unknown, revoked and malformed codes; `400 VALIDATION_FAILED` only
when `code` is missing; `429 RATE_LIMITED` – `TrackingLookup` 10 per 10 minutes per IP and `TrackingLookupGlobal` 300 per minute
for all clients together (`.RequireGlobalPostgresRateLimit(policy)`; per-client limits are checked first).

### Email outbox

1. Writer (`IEmailOutbox`, `IOrderNotifier`): rows are added to the shared unit of work and committed with the business change –
   checkout (`OrderReceived`, dedup key `order:{orderId}:received`), status changes (`order:{orderId}:history:{historyId}`) and
   resend confirmation (`order:{orderId}:resend:{n}`, n = 1, 2, … under a row lock on the order). A failed checkout rolls the row
   back with the order (integration-tested). `dedup_key` is unique: one row per business event.
2. Which status sends what (P1.7, `OrderEmailRules`): Accepted → `OrderAccepted`; Preparing → `OrderPreparing`; Shipped →
   `OrderShipped` (courier, courier tracking number and link when known; again after DeliveryFailed → Shipped); Delivered →
   `OrderDelivered`; Cancelled → `OrderCancelled`; Rejected → `OrderCancelled` with the rejection wording and the customer
   message; DeliveryFailed → nothing.
3. `payload` (jsonb) holds only what the template needs: order number, first name, dates, status, lines/totals/payment method/
   delivery address (confirmation only), shipment (shipped only), customer message, tracking page URL. The plain code is stored
   AES-256-GCM encrypted in `encrypted_token` (`version | nonce | tag | ciphertext`, the outbox row id as associated data) and set
   to `NULL` in the same update that marks the row Sent.
4. Dispatcher (`EmailDispatcher`): claims one due row at a time –
   `UPDATE … WHERE id = (SELECT … WHERE status IN ('Pending','Processing') AND next_attempt_at <= now AND (locked_until IS NULL OR
   locked_until <= now) AND attempt_count < 6 … FOR UPDATE SKIP LOCKED LIMIT 1)` → `Processing`, `attempt_count + 1`, lease
   `locked_until = now + Email:Dispatcher:LeaseSeconds` (120 s), committed at once. Then it renders, sends (timeout
   `SendTimeoutSeconds` = 30 s, shorter than the lease) and, in one transaction, writes an `email_logs` row and updates the outbox
   row guarded by `status = 'Processing' AND attempt_count = <claimed attempt>`. Two workers therefore never hold the same row
   (integration-tested with concurrent workers). A row whose lease expired during its last allowed attempt becomes Dead. Every
   run also purges expired `rate_limit_buckets`.
5. Retry policy (`EmailRetryPolicy`): after failed attempt n the next one is due after 1 min, 5 min, 30 min, 2 h, 12 h; when
   attempt 6 fails the row is `Dead`. `last_error` / `email_logs.error`: exception type, SMTP status or SES error code, message,
   with the recipient address replaced by `[primalac]`, at most 1000 characters. An admin retry resets the attempts; the log keeps
   numbering (attempt 7, 8, …).
6. Residual risk (documented): if the process dies after the provider accepted an email but before the result is recorded, the
   lease expires and the email is sent once more (bounded by the attempt limit). The result is recorded even while the host is
   stopping, which keeps the window to the moment between the provider's answer and the commit.
7. Triggers: the Lambda `Zladovce.Lambda::Zladovce.Lambda.OutboxWorkerFunction::HandleAsync` (EventBridge schedule every minute;
   at most `Email:Dispatcher:BatchSize` emails; stops claiming when less than one send timeout of the invocation is left) and,
   locally, the API's `OutboxDispatcherService` when `Email:Dispatcher:RunInProcess=true` (Development default, every 5 s; never
   inside Lambda).
8. `Email:Enabled=false`: rows are still written and stay Pending; nothing is sent (the rate-limit purge still runs).

An email failure never affects an order: checkout and status changes only insert rows; sending happens later (integration-tested
with a refusing SMTP server).

### Templates

`OrderEmailRenderer` (Application): plain C# string templates, no template library. Every data value is HTML-encoded
(`WebUtility.HtmlEncode`) where it is written; links are only emitted for absolute http(s) URLs. Table layout with inline CSS,
max 600 px, cream background, olive accents, Georgia headings, hidden preheader, plain-text alternative; no external images
except `Email:LogoUrl` (https only), no tracking pixels. Money `1.234,00 RSD`, dates `14.11.2026. u 15:30` in Europe/Belgrade.
Store contact data (email, phone, address) comes from the store settings and is shown only when set; the footer says why the
email was sent. Sender `Email:FromAddress` / `Email:FromName` (default: store name); Reply-To `Email:ReplyToAddress`, else the
store contact email, else none. Senders: `SesEmailSender` (AWSSDK.SimpleEmailV2 `SendEmail`, simple HTML + text content,
configuration set `Email:Ses:ConfigurationSetName`) and `SmtpEmailSender` (MailKit, MIT).

### Admin endpoints (AdminAny)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/v1/admin/emails` | `status` (Pending, Processing, Sent, Dead; several), `orderId`, `page`, `pageSize`; newest first |
| GET | `/api/v1/admin/emails/{id}` | `{ email, attemptLog: [{ attemptNumber, succeeded, providerMessageId, error, createdAt }] }` |
| POST | `/api/v1/admin/emails/{id}/retry` | Dead → Pending, due now, attempts reset; issues a new code when the stored one is gone. Otherwise `409 EMAIL_NOT_RETRYABLE` |
| POST | `/api/v1/admin/orders/{id}/emails/resend-confirmation` | Queues a new confirmation with a new code (earlier codes stay valid) |
| POST | `/api/v1/admin/orders/{id}/tracking/revoke` | `{ revokedCount }`; every code issued so far is revoked |

Email item: `{ id, template, status, orderId, orderNumber, recipient, attempts, lastError, nextAttemptAt, createdAt, sentAt,
isResent }` – never a body or the payload. The order detail lists the order's emails as `emails` (oldest first). Audit:
`Email.Retried`, `Order.ConfirmationResent`, `Order.TrackingRevoked`.

New error codes: `TRACKING_NOT_FOUND` (404), `EMAIL_NOT_RETRYABLE` (409).

## Images

Flow (product and category images alike; `purpose` decides the folder):

1. `POST /api/v1/admin/media/upload-url` `{ fileName, contentType, size, purpose }` → `{ uploadUrl, fields, key, expiresAt, maxSize }`.
   The pre-signed POST accepts exactly this key under `uploads/{product|category}/`, exactly this `Content-Type`
   (JPEG, PNG or WebP) and 1 byte – 10 MB.
2. The browser POSTs `multipart/form-data` to `uploadUrl`: every entry of `fields` (unchanged, in order), then the
   file as the last field named `file`. The storage answers `204`.
3. Processing (`IImageProcessor`, Magick.NET): verifies the file signature and decodes it as that format only,
   limits the pixel count, applies the EXIF orientation, converts to sRGB, strips all metadata and writes WebP
   renditions at 400, 800 and 1600 px wide (never enlarged; an image narrower than 1600 also gets its own width) to
   `media/{products|categories}/{imageId}/{width}.webp` plus a `manifest.json`, then deletes the upload. A file that is
   not an acceptable image is deleted and remembered as rejected.
   - On AWS: the image processor Lambda, triggered by the S3 upload.
   - Locally (`Media:ProcessInline=true`, the Development default): inside the attach request.
4. `POST /api/v1/admin/products/{id}/images` `{ uploadKey, altText }` (or `imageKey` in the category body) attaches
   the image after checking the renditions exist. Without inline processing the API waits up to
   `Media:ReadyWaitSeconds` for the Lambda and otherwise answers `409 IMAGE_NOT_READY` (retry).
5. Responses contain `baseUrl` and `widths`; a rendition is `{baseUrl}/{width}.webp`, so `srcset` is
   `widths.map(w => `${baseUrl}/${w}.webp ${w}w`)`.

Deleting an image, replacing or removing a category image, and **archiving a category** delete the renditions after the
database change is saved. Archiving clears `image_key` (and the image size) in the same save as the archive flag; the
files are deleted only after that save succeeded, and a storage failure is logged (storage key only, no personal data)
and leaves unused files, never a record pointing at missing files.

Image library: **Magick.NET** (`Magick.NET-Q8-AnyCPU` 15.1.0, Apache-2.0; ImageMagick itself is under the
ImageMagick licence, Apache-2.0 style). Free for commercial use; the package ships native binaries for `win-x64`,
`linux-x64`, `linux-arm64` and others.

## Lambda handlers

| Handler | Purpose | Trigger / input |
|---|---|---|
| `Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync` | `{}` or `{ "action": "migrate" }`: apply migrations as `Database__User` (migrator). `{ "action": "bootstrap" }`: create the roles (below) | Pipeline |
| `Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync` | Process uploads into renditions. No database access | S3 `s3:ObjectCreated:*` on the media bucket, prefix `uploads/` |
| `Zladovce.Lambda::Zladovce.Lambda.OutboxWorkerFunction::HandleAsync` | Send due outbox emails, purge expired rate-limit windows. Returns `{ claimed, sent, retrying, dead, expired, purgedRateLimitBuckets }` | EventBridge schedule every minute (input ignored) |

### Database roles (bootstrap)

`{ "action": "bootstrap" }` reads the RDS-managed master secret (`Database__MasterSecretArn`, JSON with `username`
and `password`) from Secrets Manager, connects to `Database__Host`/`Database__Name` as master and idempotently:

- creates `Database__User` (migrator, e.g. `zladovce_migrator`) and `Database__AppUser` (API, e.g. `zladovce_app`)
  as `LOGIN` roles without passwords and grants both `rds_iam`;
- gives the migrator `CONNECT, CREATE, TEMPORARY` on the database (CREATE is needed for the trusted extensions),
  makes it owner of schema `public` and of every table/view/sequence/function in it that the master owns;
- revokes `CREATE` on `public` from `PUBLIC`; gives the app role `USAGE` on the schema and
  `SELECT, INSERT, UPDATE, DELETE` on tables, `USAGE, SELECT, UPDATE` on sequences and `EXECUTE` on functions — for
  existing objects and, through default privileges of the migrator, for everything created later.

The app role can therefore read and write data but cannot create, alter or drop anything (integration-tested on
PostgreSQL 17). Run bootstrap once per environment before the first `migrate`; running it again is harmless.
Known gap: the app role also gets DML on `__EFMigrationsHistory` (it is a table in `public`); harmless but not minimal.

## Migrations

```powershell
cd backend
dotnet tool restore

# add a migration
dotnet ef migrations add <Name> --project src/Zladovce.Infrastructure --startup-project src/Zladovce.Infrastructure --output-dir Persistence/Migrations

# apply to the local database (or just start the API in Development)
dotnet ef database update --project src/Zladovce.Infrastructure --startup-project src/Zladovce.Infrastructure
```

The tools connect to the Docker Compose database; set `ZLADOVCE_DB_CONNECTION` to target another one.

Outside Development the API **never** migrates at start-up. Deployed environments are migrated by the
`MigrationRunner` Lambda (`Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync`), which the pipeline
invokes with `{}` before publishing a new API version. It returns `{ action, appliedMigrations, currentMigration }`
and throws (failed invocation) on error. It is configured through environment variables
(`Database__UseIamAuth`, `Database__Host`, ...) with the migrator role (`Database__User`). See "Database roles"
above for the one-off `bootstrap` action.

Migrations must stay backward compatible with the previously deployed code (add first, remove later).

## Tests

```powershell
cd backend
dotnet build Zladovce.sln
dotnet test Zladovce.sln
```

- Unit tests need nothing.
- Integration tests need Docker: Testcontainers starts a throw-away `postgres:17-alpine`, the schema is
  created by the real `MigrationRunner` handler, and the API is hosted in memory with
  `WebApplicationFactory`. They never touch the Docker Compose database.
- The Cognito path is tested with locally signed JWTs shaped like Cognito access tokens; only the signing
  key source is replaced, all validation rules are the real ones. Nothing in the tests calls AWS.
- Email tests run the real outbox worker handler against a real Mailpit container (`axllent/mailpit:v1.32.0`); failures come
  from a local TCP server that answers `554`, so the whole MailKit send path runs. A fake clock drives the back-off.
- Media tests run against a real MinIO container (`cgr.dev/chainguard/minio`, same as compose): they upload with
  the real pre-signed POST and check the real WebP renditions. Storage is never mocked.
- The role bootstrap is tested on the PostgreSQL container (a `rds_iam` role is created there to mimic RDS; the
  test gives the roles passwords because IAM tokens do not exist locally). Secrets Manager is replaced by a fixed
  credentials provider.
- Tests create their own data with unique names; there is no seed catalog.

## OpenAPI document

`openapi/zladovce-v1.json` is generated from the code at build time (no running API or database needed):

```powershell
pwsh backend/scripts/generate-openapi.ps1     # or: powershell -File backend/scripts/generate-openapi.ps1
```

Run it after changing endpoints or DTOs and commit the file. Note: the generator starts the application host in
Development, so it also applies pending migrations to the local Docker database when that is running.

## Configuration

Standard ASP.NET Core configuration: `appsettings.json`, `appsettings.{Environment}.json`, environment
variables (`Section__Key`).

| Key | Meaning |
|---|---|
| `Database:ConnectionString` | Npgsql connection string (local development, tests) |
| `Database:UseIamAuth` | `true` = RDS IAM authentication; the connection string is ignored |
| `Database:Host`, `Port`, `Name`, `User`, `Region` | Database endpoint, database name, database role, AWS region (IAM mode) |
| `Database:RootCertificatePath` | Optional RDS CA bundle; when set TLS is `VerifyFull`, otherwise `Require` |
| `Database:MaxPoolSize` | Optional connection pool limit per process (keep small on Lambda) |
| `Database:MigrateOnStartup` | Development only: apply migrations when the API starts. Ignored elsewhere |
| `Auth:Cognito:Region`, `UserPoolId`, `ClientId` | User pool and admin app client whose access tokens are accepted |
| `Auth:DevBypass` | Development only: header-based development authentication (below) |
| `Cors:AllowedOrigins` | Exact origins allowed to call the API. No wildcards, no paths |
| `Serilog:*` | Optional overrides, e.g. `Serilog:MinimumLevel:Default` |
| `Database:MasterSecretArn`, `Database:AppUser` | Migration Lambda `bootstrap` only: master secret, application role |
| `Media:BucketName` | Media bucket (`uploads/` private, `media/` public) |
| `Media:BaseUrl` | Public base URL of the bucket (CloudFront on AWS; `http://localhost:9000/zladovce-media-dev` locally) |
| `Media:Region` | Optional bucket region (defaults to the Lambda's region) |
| `Media:ServiceUrl`, `ForcePathStyle`, `AccessKey`, `SecretKey` | S3-compatible endpoint (MinIO) — local development only; on AWS leave empty and the execution role is used |
| `Media:ProcessInline` | `true` = the attach endpoint processes uploads itself (Development); `false` = the image Lambda does it |
| `Media:ReadyWaitSeconds` | How long attach waits for the image Lambda (default 8, clamped to 0–25). On AWS it must stay below the API Lambda timeout (15 s) and the 30 s HTTP API limit; the CDK configuration validates this |
| `Media:UploadUrlLifetimeMinutes` | Lifetime of a pre-signed upload (default 10) |
| `Catalog:MaxOrderQuantity` | Cap of `maxOrderQuantity` in the shop (default 20) |
| `Catalog:BestsellerWindowDays` | Days of delivered orders counted for bestsellers (default 90) |
| `RateLimiting:Enabled` | `false` switches every rate limit off (default `true`) |
| `RateLimiting:Policies:CreateOrder:PermitLimit`, `WindowSeconds` | Checkout limit per client IP (default 5 per 600 s) |
| `ClientIp:TrustedProxyHops` | Trusted proxies appending to `X-Forwarded-For` (default 1 = API Gateway; 0 = connection address only) |
| `RateLimiting:Policies:TrackingLookup`, `TrackingLookupGlobal` | Tracking lookup: 10 per 600 s per IP; 300 per 60 s for all clients together |
| `Storefront:BaseUrl` | Public shop URL for links in emails (`http://localhost:4200` in Development). Required (https) outside Development |
| `Tracking:TokenEncryptionKeySecretArn` | Secrets Manager secret with the AES-256 key (secret string = base64 of 32 bytes, or `{ "key": "<base64>" }`). AWS |
| `Tracking:TokenEncryptionKey` | Base64 of 32 bytes instead of the secret (local, tests). Development has a fixed public key, refused elsewhere |
| `Email:Enabled` | `false` = outbox rows are written but nothing is sent (default `true`) |
| `Email:Provider` | `Ses` (default) or `Smtp` (Development: Mailpit) |
| `Email:FromAddress`, `FromName`, `ReplyToAddress` | Sender (verified SES identity), display name (default: store name), Reply-To (default: store contact email) |
| `Email:LogoUrl` | Optional https URL of the logo in the email header (the only image) |
| `Email:Ses:ConfigurationSetName`, `Region` | SES configuration set; SES region (default: the Lambda's region) |
| `Email:Smtp:Host`, `Port`, `Security` (`None`/`StartTls`/`SslOnConnect`), `UserName`, `Password` | SMTP (Development: `localhost:1025`, `None`) |
| `Email:Dispatcher:RunInProcess`, `IntervalSeconds` | Background dispatcher in the API (Development: `true`, 5 s). Must be false on Lambda |
| `Email:Dispatcher:BatchSize`, `LeaseSeconds`, `SendTimeoutSeconds` | Emails per run (50), lease of a claimed row (120 s), send timeout (30 s) |

Nothing the API needs depends on the environment being called `Development`: deployed environments (`Dev`,
`Staging`, `Production`) only differ in configuration; Swagger, start-up migration and the development
authentication exist only in `Development` (integration-tested for `Dev`, `Staging` and `Production`).

No secrets belong in these files. In IAM mode the API holds no database password: for each new connection
it signs a short-lived token locally with the Lambda role (`RDSAuthTokenGenerator`), reused for 10 minutes.

The application refuses to start when:

- `Auth:DevBypass` is `true` outside the Development environment, or inside AWS Lambda;
- `Auth:DevBypass` is off and the Cognito settings are incomplete;
- `Cors:AllowedOrigins` contains anything that is not an exact origin;
- outside Development: `Storefront:BaseUrl` is not an https URL, no tracking key source is set, the configured key is not 32
  bytes or is the public development key, or `Email:Dispatcher:RunInProcess` is true inside Lambda;
- in a host that sends (the outbox worker, or the API with `RunInProcess`): `Email:FromAddress` is missing while
  `Email:Enabled` is true. The outbox worker Lambda runs the same checks at start.

## Authentication and authorization

Roles: `Owner` and `Staff`. Policies: `AdminAny` (Owner or Staff) and `OwnerOnly`. Everything under
`/api/v1/admin` requires `AdminAny` at least.

### Cognito (all deployed environments)

`Authorization: Bearer <Cognito access token>`. The API validates signature (user pool JWKS), issuer,
lifetime, `token_use = access` and `client_id`. The role comes from `cognito:groups` (`Owner` wins over
`Staff`); a token without an administrator group gets `403`.

After authorization the token is mapped to an `admin_users` row by `cognito_sub`:

- **First valid sign-in creates the row** (auto-provisioning) and writes an `Auth.Login` audit entry.
  Cognito access tokens do not contain the email, so on that first request only the API calls Cognito
  `GetUser` with the caller's own access token to read `email` and `name`. This needs no IAM permission, but
  the token must carry the scope `aws.cognito.signin.user.admin` (tokens from the SRP sign-in used by the
  admin application do) and the Lambda must be able to reach Cognito. If the token itself has an `email`
  claim, that is used and Cognito is not called.
- If no email can be determined, or the email already belongs to a different identity: `403 ADMIN_NOT_PROVISIONED`.
- An inactive row (`is_active = false`): `403 ADMIN_INACTIVE`.
- The stored role follows the token's group on every request; `last_login_at` is refreshed at most hourly.

Who becomes an administrator is therefore controlled in Cognito (no self-registration, groups assigned by
the Owner); deactivating the `admin_users` row blocks a user in the API immediately.

### Development authentication (local only)

With `ASPNETCORE_ENVIRONMENT=Development` **and** `Auth:DevBypass=true` (the default in
`appsettings.Development.json`) requests can authenticate with two headers instead of a token:

```
X-Dev-User-Email: owner@example.com
X-Dev-User-Role: Owner            (or Staff)
```

The admin row is created on first use with `cognito_sub = dev:<email>`. Invalid values give `401`.

Why it cannot be enabled in production:

1. The scheme is registered in exactly one place, behind `IsDevelopment() && Auth:DevBypass`. In any other
   environment the handler does not exist and the headers are ignored (integration-tested).
2. A start-up guard throws if `Auth:DevBypass` is `true` outside Development, and also if it is `true`
   inside AWS Lambda regardless of the environment name, so a misconfigured deployment fails to start
   instead of running open (unit- and integration-tested).
3. `appsettings.json` (the base for deployed environments) has `DevBypass: false`; only
   `appsettings.Development.json` turns it on.

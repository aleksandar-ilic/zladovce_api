# ZLADOVCE – API SPECIFICATION

> Part of the Zladovce master specification. Related documents:
> [PROJECT_SPECIFICATION](PROJECT_SPECIFICATION.md) · [ARCHITECTURE](ARCHITECTURE.md) · [DATABASE](DATABASE.md) · [AWS_INFRASTRUCTURE](AWS_INFRASTRUCTURE.md) · [IMPLEMENTATION_PLAN](IMPLEMENTATION_PLAN.md)

The master specification does not contain a dedicated API section. The full endpoint specification is a Phase 1 deliverable (see [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md)). This document collects the API requirements stated in the master specification.

---

## General requirements

- ASP.NET Core Web API, REST API. (§2)
- Swagger / OpenAPI. (§2)
- FluentValidation for request validation. (§2, §3)
- DTOs, centralized exception handling, consistent API response formats. (§3)
- Hosted on AWS Lambda behind Amazon API Gateway. (§2)

## Security

- Amazon Cognito token validation for admin endpoints; role-based backend authorization. (§15)
- Never trust frontend authorization alone. (§15)
- Rate limiting, input validation, secure CORS, XSS and CSRF protection where applicable. (§15)
- Never expose tracking token hashes or unnecessary customer information. (§15)

## Checkout and orders

- Validate all information on the backend. (§7)
- Recalculate prices and availability using the database; do not trust prices or stock quantities submitted by the frontend. (§6, §7)
- Duplicate submission protection and idempotency for order creation; never create duplicate orders due to repeated requests. (§7)
- Transactional order creation with stock reservation or deduction. (§7)
- Controlled order status transitions; invalid transitions are rejected. (§8)

## Public order tracking

- Public route `/track-order` accepting the internal Zladovce tracking code. (§9)
- Tracking token validation, rate limiting, generic invalid-code response, protection against enumeration. (§9)
- Response exposes only: order number, current status, creation date, status timeline with timestamps, customer-facing messages, courier information when available. (§9)

## Admin

- Endpoints for dashboard/analytics, orders, products, categories, inventory, blog, CMS, customers, reports, store settings, admin users and audit log. (§12, §13)
- Pre-signed S3 URLs for direct secure image uploads. (§12)
- Stock adjustments require a mandatory reason and are recorded. (§12)
- Analytics use real backend data with date filters: today, yesterday, last 7 days, last 30 days, current month, previous month, current year, custom range. (§12)

## Public content

- Unpublished blog/CMS content must never be exposed publicly. (§13)

---

## Endpoints

Defined below (Phase 1 design, not part of the original master specification).

### Conventions

- Base path: `/api/v1`. JSON, camelCase, UTF-8.
- Public endpoints need no authentication. Everything under `/api/v1/admin` needs a Cognito access token (`Authorization: Bearer …`). **O** = Owner only; otherwise Owner and Staff.
- Money is a JSON number with two decimals, currency always RSD. Timestamps are ISO 8601 in UTC.
- Lists: query `page` (from 1), `pageSize` (default 20, max 100), `sort`. Response:

```json
{ "items": [], "page": 1, "pageSize": 20, "totalItems": 0, "totalPages": 0 }
```

- Errors: RFC 9457 Problem Details with a stable machine code and a Serbian message.

```json
{
  "type": "https://zladovce.com/errors/insufficient-stock",
  "title": "Nedovoljno na stanju",
  "status": 409,
  "code": "INSUFFICIENT_STOCK",
  "detail": "Nekih proizvoda više nema u traženoj količini.",
  "errors": { "items[0].quantity": ["Dostupno: 2"] },
  "traceId": "…"
}
```

| Status | Used for |
|---|---|
| 400 | Validation failed (`errors` lists fields) |
| 401 / 403 | Not signed in / role not allowed |
| 404 | Not found (also unpublished content) |
| 409 | Business conflict: stock, invalid status transition, concurrent edit, duplicate slug or SKU |
| 429 | Rate limit |

### Public – catalog

| Method | Path | Purpose |
|---|---|---|
| GET | `/catalog/categories` | Active categories |
| GET | `/catalog/categories/{slug}` | Category with SEO data |
| GET | `/catalog/products` | Product grid. Query: `category`, `q`, `minPrice`, `maxPrice`, `inStock`, `featured`, `sort` (`newest`, `priceAsc`, `priceDesc`, `name`, `bestselling`), paging |
| GET | `/catalog/products/{slug}` | Product detail with variants, prices, availability, images |
| GET | `/catalog/products/{slug}/related` | Related products (same category) |
| GET | `/catalog/bestsellers` | Best-selling products from real delivered orders |
| GET | `/catalog/availability?variantIds=…` | Live price and availability for prerendered pages |

Public availability is exposed as `inStock` plus `maxOrderQuantity`, not as the exact stock figure.

### Public – cart, checkout, tracking

| Method | Path | Purpose |
|---|---|---|
| POST | `/cart/quote` | Body: `items[{variantId, quantity}]`. Returns priced lines, adjustments (unavailable or reduced items), subtotal, shipping cost, total, minimum-order check |
| POST | `/orders` | Create order. Header `Idempotency-Key: <uuid>` required. Body: `items[{variantId, quantity}]`, `customer{firstName, lastName, email, phone}`, `address{city, street, postalCode}`, `deliveryNote`, `acceptedTerms`. Returns `201` with `orderNumber`, totals and `trackingCode` (shown once). Replays return the original order with `200` and no tracking code |
| POST | `/tracking/lookup` | Body: `code`. Returns order number, status, created date, timeline (status, time, customer message), courier name, courier tracking number and URL when available. Any failure returns the same `404` `TRACKING_NOT_FOUND` |

### Public – content and store

| Method | Path | Purpose |
|---|---|---|
| GET | `/store` | Public store settings: name, logo, contact, social links, delivery info, shipping price and thresholds, SEO defaults |
| GET | `/content/pages/{key}` | `home`, `about`, `privacy`, `terms` |
| GET | `/content/faq` | Published FAQ items |
| GET | `/blog/categories` | Blog categories |
| GET | `/blog/posts` | Published posts. Query: `category`, paging |
| GET | `/blog/posts/{slug}` | Published post |
| GET | `/seo/routes` | Slugs and last-modified dates for prerendering and sitemap |
| POST | `/newsletter/subscriptions` | Body: `email`, `consent` |
| POST | `/contact` | Body: `name`, `email`, `message`. Queued as an email to the store address |

### Admin – session and dashboard

| Method | Path | Purpose |
|---|---|---|
| GET | `/admin/me` | Current administrator and role |
| GET | `/admin/dashboard/summary` | Query: `period` (`today`, `yesterday`, `last7`, `last30`, `thisMonth`, `lastMonth`, `thisYear`, `custom`) + `from`, `to`. Returns ordered value, delivered value, estimated collected, actual collected, expenses, estimated profit, average order value, counts per status |
| GET | `/admin/dashboard/revenue-series` | Revenue over time |
| GET | `/admin/dashboard/order-series` | Order volume over time |
| GET | `/admin/dashboard/top-products` | Best sellers |
| GET | `/admin/dashboard/sales-by-category` | Sales by category |
| GET | `/admin/dashboard/status-distribution` | Orders per status |
| GET | `/admin/dashboard/low-stock` | Variants at or below threshold |

### Admin – orders

| Method | Path | Purpose |
|---|---|---|
| GET | `/admin/orders` | Query: `q` (number, name, email, phone), `status`, `from`, `to`, sort, paging |
| GET | `/admin/orders/{id}` | Detail: customer, address, items, totals, timeline, shipments, emails, allowed next statuses |
| POST | `/admin/orders/{id}/status` | Body: `newStatus`, `internalNote`, `customerMessage`, `shipment{courierCompanyId, trackingNumber, shippedAt, note}` (required for Shipped), `restock` (when cancelling after a failed delivery), `version`. `409` on invalid transition |
| PUT | `/admin/orders/{id}/shipments/{shipmentId}` | Correct courier, tracking number, date, note |
| POST | `/admin/orders/{id}/notes` | Add an internal note without changing status |
| PUT | `/admin/orders/{id}/payment` | Mark amount actually collected and date, or clear it |
| POST | `/admin/orders/{id}/emails/resend-confirmation` | Queues a new confirmation with a new tracking code |
| POST | `/admin/emails/{outboxId}/retry` | Retry a failed email |

### Admin – catalog and inventory

| Method | Path | Purpose |
|---|---|---|
| GET, POST | `/admin/categories` | List, create |
| GET, PUT, DELETE | `/admin/categories/{id}` | Read, update, archive (refused while it has active products) |
| GET, POST | `/admin/products` | List (query: `q`, `category`, `status`, `archived`), create with first variant |
| GET, PUT | `/admin/products/{id}` | Read, update |
| POST | `/admin/products/{id}/archive`, `/restore` | Archive, restore |
| POST | `/admin/products/{id}/variants` | Add variant |
| PUT, DELETE | `/admin/products/{id}/variants/{variantId}` | Update, archive variant |
| POST | `/admin/media/upload-url` | Body: `fileName`, `contentType`, `size`, `purpose`. Returns pre-signed POST fields |
| POST | `/admin/products/{id}/images` | Attach an uploaded image |
| PUT | `/admin/products/{id}/images/order` | Reorder and set main image |
| PUT, DELETE | `/admin/products/{id}/images/{imageId}` | Alt text, remove |
| GET | `/admin/inventory` | Query: `q`, `status` (`ok`, `low`, `out`), paging |
| POST | `/admin/inventory/{variantId}/adjustments` | Body: `quantityDelta`, `reason` (required) |
| PUT | `/admin/inventory/{variantId}/threshold` | Low-stock threshold |
| GET | `/admin/inventory/{variantId}/movements` | Movement history |

### Admin – content

| Method | Path | Purpose |
|---|---|---|
| GET, PUT | `/admin/cms/pages/{key}` | Homepage, About, privacy, terms |
| GET, POST, PUT, DELETE | `/admin/faq`, `/admin/faq/{id}` | FAQ items; `PUT /admin/faq/order` to reorder |
| GET, POST, PUT, DELETE | `/admin/blog/categories`, `/{id}` | Blog categories |
| GET, POST | `/admin/blog/posts` | List (drafts included), create |
| GET, PUT, DELETE | `/admin/blog/posts/{id}` | Read, update, archive |
| POST | `/admin/blog/posts/{id}/publish`, `/unpublish` | Change publication state |

### Admin – customers, reports, settings, users, audit

| Method | Path | Role | Purpose |
|---|---|---|---|
| GET | `/admin/customers` | | List with order count and delivered value |
| GET | `/admin/customers/{id}` | | Customer with orders |
| GET | `/admin/reports/sales` | | Sales by product and category for a period; `format=csv` to export |
| GET | `/admin/reports/inventory` | | Stock report; `format=csv` |
| GET, POST, PUT, DELETE | `/admin/expenses`, `/{id}` | O | Expenses |
| GET, PUT | `/admin/settings` | O | Store settings |
| GET, POST, PUT | `/admin/couriers`, `/{id}` | O | Courier companies (deactivate instead of delete) |
| GET, POST | `/admin/users` | O | List, invite administrator (creates Cognito user) |
| PUT | `/admin/users/{id}` | O | Change role, activate, deactivate |
| GET | `/admin/audit-logs` | O | Query: `adminUserId`, `entityType`, `from`, `to`, paging |
| GET | `/admin/emails` | | Email outbox and attempts; query: `status`, `orderId` |

### Rate limits (initial values, tuned in Phase 8)

| Endpoint | Limit per IP |
|---|---|
| `POST /tracking/lookup` | 10 per 10 minutes |
| `POST /orders` | 5 per 10 minutes |
| `POST /contact`, `POST /newsletter/subscriptions` | 3 per 10 minutes |
| Other public GET | API Gateway throttling only |

### OpenAPI

The API publishes an OpenAPI document. Swagger UI is enabled in development and staging only. The frontend types in `libs/models` are generated from this document.

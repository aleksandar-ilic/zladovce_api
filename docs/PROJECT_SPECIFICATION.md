# ZLADOVCE – PROJECT SPECIFICATION
## Production-Ready E-commerce Platform

> Part of the Zladovce master specification. Related documents:
> [ARCHITECTURE](ARCHITECTURE.md) · [DATABASE](DATABASE.md) · [API_SPECIFICATION](API_SPECIFICATION.md) · [AWS_INFRASTRUCTURE](AWS_INFRASTRUCTURE.md) · [IMPLEMENTATION_PLAN](IMPLEMENTATION_PLAN.md)

You are acting as a principal software architect, senior .NET engineer, senior Angular engineer, AWS cloud architect, database architect, and senior UI/UX designer.

Your responsibility is to design and implement a complete, production-ready e-commerce platform for a Serbian family-owned business called Zladovce.

You must think and work like an experienced engineering team.

Do not simply generate code. Make sound architectural decisions, maintain high code quality, implement real functionality, and ensure the application is maintainable and secure.

---

# 1. BUSINESS CONTEXT

Business name: Zladovce

Existing website:
https://www.zladovce.com/

Design inspiration:
https://www.jajce.rs/

Zladovce is a family-owned business from southern Serbia selling homemade and natural products.

Products include:
- Homemade jams
- Natural fruit juices
- Homemade vinegar
- Compotes
- Fruit products
- Seasonal homemade products

The goal is to transform the existing website into a complete modern online store.

The platform will be used by:
1. Customers purchasing products.
2. Business administrators managing the entire shop.

The primary administrator is a non-technical family member. Therefore, the administrative experience must be extremely simple and intuitive.

## Business Rules

These requirements are final and must be respected:

- Target market: Entire Serbia.
- Currency: Serbian dinar (RSD).
- Customer-facing language: Serbian Latin.
- Payment method: Cash on delivery (pouzećem).
- Customer registration: Not required.
- Customer accounts: Not part of the MVP.
- Administrator authentication: Required.
- Delivery management: Manual.
- Courier company: Entered manually by administrator.
- Courier tracking number: Entered manually by administrator.
- Internal order tracking: Generated automatically.
- Transactional emails: Required.
- Online card payments: Not included in MVP.

Do not implement unnecessary customer authentication or online payment integrations.

Design the architecture so these features can be added in the future without major refactoring.

---

# 4. DESIGN SYSTEM AND BRAND IDENTITY

Create a premium, modern, authentic visual identity.

Zladovce should feel like an established independent food brand.

Design direction:
- Warm cream backgrounds.
- Olive green.
- Sage green.
- Natural earthy colors.
- Elegant typography.
- Large product photography.
- Subtle animations.
- Generous whitespace.
- Rounded but restrained UI elements.
- Premium product cards.
- Beautiful responsive navigation.

Avoid:
- Generic e-commerce templates.
- Excessive gradients.
- Excessive animations.
- Overcrowded layouts.
- Unnecessary decorative elements.

The design must communicate:
- Family tradition.
- Natural products.
- Trust.
- Authenticity.
- Quality.
- Homemade production.

All customer-facing and administrative UI text must be in Serbian Latin.

Use realistic Serbian copywriting.

Never use Lorem Ipsum.

Do not invent business contact information, certifications, awards, product ingredients, or product claims. Use configurable placeholders where actual business information is unavailable.

---

# 5. PUBLIC STOREFRONT

Implement these pages:

1. Homepage
2. Shop
3. Product category
4. Product details
5. Shopping cart
6. Checkout
7. Order confirmation
8. Order tracking
9. About us
10. Contact
11. FAQ
12. Blog listing
13. Blog article
14. Privacy policy
15. Terms and conditions

## Homepage

Include:
- Premium hero section.
- Brand introduction.
- Featured categories.
- Featured products.
- Best-selling products.
- Family story.
- Why choose our products.
- Product quality section.
- Customer testimonials, only when real testimonials are provided.
- Blog previews.
- Newsletter section.
- Footer with navigation and contact information.

## Navigation

Implement:
- Logo.
- Main navigation.
- Shop.
- About us.
- Blog.
- Contact.
- Shopping cart with item count.
- Responsive mobile navigation.

## Shop

Implement:
- Product grid.
- Category filters.
- Price filters.
- Search.
- Sorting.
- Pagination.
- Availability indicator.
- Featured products.

## Product Details

Display:
- Product images.
- Product name.
- Description.
- Price.
- Discount price.
- Available variants.
- Stock availability.
- Quantity selector.
- Add to cart.
- Related products.

Do not allow customers to purchase unavailable quantities.

---

# 6. PRODUCT AND INVENTORY MANAGEMENT

Products must support:

- Name.
- Slug.
- Short description.
- Full description.
- SKU.
- Category.
- Main image.
- Additional images.
- Regular price.
- Discounted price.
- Measurement unit.
- Product variants.
- Active/inactive status.
- Featured status.
- SEO title.
- SEO description.
- Created and updated timestamps.

Examples of variants:
- Jam 370g.
- Jam 720g.
- Juice 1L.
- Vinegar 0.5L.

Each variant must have its own:
- SKU.
- Price.
- Stock quantity.
- Availability.

Inventory features:
- Current stock.
- Stock adjustments.
- Stock movement history.
- Low-stock threshold.
- Low-stock alerts.
- Inventory reports.

Every stock change must be recorded.

Use database transactions and appropriate concurrency control to prevent overselling.

Do not trust prices or stock quantities submitted by the frontend.

Product and order prices must use appropriate decimal types, never floating-point types.

---

# 7. SHOPPING CART AND CHECKOUT

Customers must be able to purchase without registration.

Implement a persistent guest cart using an appropriate client-side storage strategy.

Checkout fields:

- First name.
- Last name.
- Email.
- Phone number.
- City.
- Street and house number.
- Postal code.
- Delivery notes.

Payment:
- Cash on delivery only.

Display:
- Product name.
- Variant.
- Quantity.
- Unit price.
- Subtotal.
- Shipping cost.
- Final total.

Validate all information on the backend.

Recalculate prices and availability using the database.

Implement:
- Checkout validation.
- Duplicate submission protection.
- Idempotency for order creation.
- Transactional order creation.
- Stock reservation or deduction.
- Correct handling of cancelled orders.

A customer must receive a clear confirmation after successful order creation.

Never create duplicate orders due to repeated requests.

---

# 8. ORDER MANAGEMENT

Order number format:

ZLD-YYYY-000001

Generate unique, sequential-looking order references while ensuring database-level uniqueness.

Order statuses:

- Received
- Accepted
- Preparing
- Shipped
- Delivered
- Cancelled
- Rejected
- DeliveryFailed

Use Serbian labels in the UI:

- Primljena
- Prihvaćena
- U pripremi
- Poslata
- Isporučena
- Otkazana
- Odbijena
- Neuspešna isporuka

Implement a controlled state transition system.

Every transition must:
- Be validated.
- Be recorded.
- Store the previous status.
- Store the new status.
- Store timestamp.
- Store administrator identity.
- Support internal notes.
- Support customer-facing messages.

Do not allow invalid status transitions.

Store order item snapshots:
- Product name.
- SKU.
- Variant.
- Unit price.
- Quantity.
- Line total.

Historical orders must never change when product information is edited.

---

# 9. ORDER TRACKING

Implement a public route:

/track-order

Customers enter their internal Zladovce tracking code.

Generate cryptographically secure random tokens with sufficient entropy.

Store only a secure hash of the token.

The original token must be available to the customer through the order confirmation email.

Implement:
- Tracking token validation.
- Rate limiting.
- Generic invalid-code response.
- Protection against enumeration.
- No unnecessary exposure of personal information.

The tracking page displays:

- Order number.
- Current status.
- Order creation date.
- Timeline of previous statuses.
- Status timestamps.
- Customer-facing messages.
- Courier information when available.

Example timeline:

1. Porudžbina primljena.
2. Porudžbina prihvaćena.
3. Proizvodi se pripremaju.
4. Pošiljka predata kurirskoj službi.
5. Porudžbina isporučena.

The timeline must reflect actual recorded events, not hardcoded sample data.

---

# 10. SHIPPING MANAGEMENT

Shipping is nationwide within Serbia.

Initially, all courier management is manual.

Create a shipping management module.

Administrator can:
- Configure standard delivery price.
- Configure free delivery threshold.
- Configure minimum order value.
- Enable or disable shipping.
- Set estimated delivery text.
- Select courier company.
- Enter external courier tracking number.
- Enter shipment date.
- Add shipping notes.

Support courier companies as configurable database records.

The courier tracking number is different from the internal Zladovce tracking token.

Never assume that an external courier tracking API is available.

Where a verified tracking URL template exists, allow the administrator to configure it.

Do not hardcode courier integrations.

Keep the design extensible for future API integrations.

Shipping price must be stored as a snapshot in each order.

---

# 11. EMAIL NOTIFICATIONS

Use Amazon SES.

Create responsive HTML email templates in Serbian Latin.

Required emails:

1. Order received.
2. Order accepted.
3. Order being prepared.
4. Order shipped.
5. Order delivered.
6. Order cancelled.

The initial order confirmation must include:

- Order number.
- Customer name.
- Ordered products.
- Quantities.
- Prices.
- Shipping cost.
- Total.
- Payment method.
- Internal tracking code.
- Tracking page link.

When the order is shipped, include courier company and courier tracking number if available.

Implement:
- Email logging.
- Delivery attempt records.
- Retry handling.
- Failure logging.
- Idempotent notification processing.

Email failure must never cause an otherwise successful order to be lost.

Use a transactional outbox pattern or another reliable dispatch mechanism appropriate for the architecture.

Do not send duplicate notifications for the same event.

---

# 12. ADMINISTRATIVE APPLICATION

Create a separate, secure administration interface.

Only authorized administrators can access it.

The admin application must be optimized for non-technical users.

Navigation:

- Dashboard.
- Orders.
- Products.
- Categories.
- Inventory.
- Blog.
- CMS.
- Customers.
- Reports.
- Store settings.
- Admin users.
- Audit log.

## Dashboard

Display:

- Total order value.
- Delivered order revenue.
- Orders awaiting confirmation.
- Orders in preparation.
- Orders awaiting shipment.
- Delivered orders.
- Cancelled orders.
- Average order value.
- Best-selling products.
- Low-stock products.

Revenue must be calculated correctly.

Since MVP uses cash on delivery, clearly distinguish:
- Ordered value.
- Delivered order value.
- Estimated collected revenue.
- Actual collected payments, if verified.
- Expenses.
- Estimated profit.

Do not treat every newly created order as collected revenue.

Implement filters:
- Today.
- Yesterday.
- Last 7 days.
- Last 30 days.
- Current month.
- Previous month.
- Current year.
- Custom date range.

Charts:
- Revenue over time.
- Order volume.
- Best-selling products.
- Sales by category.
- Order status distribution.

All analytics must use real backend data.

## Order Management UI

Implement:
- Search.
- Filters.
- Sorting.
- Pagination.
- Order status badges.
- Order detail page.
- Customer information.
- Product information.
- Order timeline.
- Shipping information.
- Internal notes.
- Status updates.

Make common actions easy and obvious.

## Product Management UI

Implement:
- Product list.
- Product creation.
- Product editing.
- Product archiving.
- Variant management.
- Multiple image uploads.
- Image preview.
- Stock management.
- Price management.
- Category selection.
- Product visibility.

Use direct secure S3 uploads through pre-signed URLs.

## Inventory UI

Display:
- Product.
- Variant.
- SKU.
- Current stock.
- Minimum stock.
- Stock status.

Allow authorized administrators to adjust stock with mandatory reasons.

Record all adjustments.

## Store Settings

Create an intuitive configuration screen for:
- Store name.
- Logo.
- Contact email.
- Phone.
- Business address.
- Delivery price.
- Free delivery threshold.
- Minimum order amount.
- Delivery information.
- Social media links.
- SEO defaults.

Do not hardcode configurable business information.

---

# 13. BLOG AND CMS

Implement a simple CMS.

Administrators must be able to manage:

- Homepage content.
- About us.
- FAQ.
- Blog categories.
- Blog posts.
- Images.
- SEO metadata.

Blog post fields:
- Title.
- Slug.
- Excerpt.
- Cover image.
- Rich text content.
- Category.
- Publication status.
- Publication date.
- SEO title.
- SEO description.

Support draft and published states.

Do not expose unpublished content publicly.

---

# 16. SEO AND PERFORMANCE

Implement:
- SEO metadata.
- Product structured data.
- Open Graph metadata.
- Canonical URLs.
- Sitemap.xml.
- Robots.txt.
- Clean product URLs.
- Optimized images.
- Lazy loading.
- Responsive image sizes.
- Performance optimization.
- Accessible semantic HTML.

Use SSR or prerendering for public SEO-critical pages where appropriate.

Use WebP or AVIF images when suitable.

Ensure good Core Web Vitals.

---

# 20. IMPORTANT ENGINEERING RULES

- Prioritize working functionality over decorative UI.
- Never hardcode production metrics.
- Never use fake production services.
- Never trust client-side prices.
- Never expose customer data unnecessarily.
- Never silently ignore failed operations.
- Never leave non-functional buttons.
- Never create duplicate orders.
- Never allow invalid stock operations.
- Never allow invalid order status transitions.
- Never lose an order because an email failed.
- Never store plaintext tracking tokens.
- Never expose secrets in source code.
- Never introduce unnecessary microservices.

Use clear naming conventions and maintainable code.

Keep documentation updated.

Make the application easy to extend.

When a requirement is ambiguous, document the assumption and ask only when the decision materially affects implementation.

---
---

# PHASE 1 – DISCOVERY FINDINGS AND FUNCTIONAL DECISIONS

Everything below this line is Phase 1 output (design), not part of the original master specification. Assumptions and open business questions are listed in [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

## P1.1 Existing website (zladovce.com)

Reviewed through an automated read of the homepage only; the cart and checkout were not exercised.

| Area | Finding | Consequence |
|---|---|---|
| Platform | Next.js site, Serbian Latin | Replaced entirely by the Angular storefront |
| Navigation | Početna, Prodavnica, O nama (anchor on homepage), Korpa | New site adds Blog, Kontakt, FAQ, tracking, legal pages |
| Catalog | 5 products shown: Džem od borovnice (890 RSD), Džem od maline (850 RSD), Sok od borovnice (720 RSD), Kompot od jabuke i borovnice (650 RSD), Sveža borovnica 1 kg (1.200 RSD) | Used only as an indication of range and price level; real catalog must be supplied by the business |
| Categories | Džemovi, Sokovi, Kompoti, Sveže voće | Seed categories; vinegar category to be added |
| Package sizes | Not shown, except 1 kg for fresh blueberries | Variants (370 g, 720 g, 1 L …) must be confirmed |
| URLs | `/prodavnica`, `/proizvod/{numeric id}` | Keep `/prodavnica`; add 301 redirects from old `/proizvod/{id}` to new slug URLs |
| Photos | Product photos are served from Unsplash (stock) | Real product photography is required before launch |
| Claims | "100% organski", "organski proizvodi" | The word "organski" is not carried over unless the business confirms certification (see open questions) |
| Contact | No email, phone or address shown | All contact data comes from Store Settings; nothing invented |
| Story | Family farm in Balčinci mahala, Zladovce; mother prepares jams, juices, compotes from blueberries, blackberries, raspberries | Basis for the "O nama" copy, to be confirmed by the family |

## P1.2 Design reference (jajce.rs)

What is worth adopting, adapted to a multi-product shop:

- Photo-led hero with one clear headline and two calls to action.
- Numbered, short homepage sections with a personal tone and generous spacing.
- "How it works" in three steps (izaberi – poruči – stiže na adresu).
- Origin/family section with real photos.
- FAQ on the homepage, covering delivery, payment on delivery and "no account needed".
- Order without an account, payment on delivery, tracking link after ordering.

Not adopted: single-product package calculator, city toggle, dark stone palette (Zladovce uses cream, olive and sage).

## P1.3 Storefront route map

| Page | Route | Rendering |
|---|---|---|
| Homepage | `/` | Prerendered |
| Shop | `/prodavnica` | Prerendered shell, client filters |
| Category | `/prodavnica/:categorySlug` | Prerendered |
| Product | `/proizvod/:slug` | Prerendered, price and stock refreshed in the browser |
| Cart | `/korpa` | Client only |
| Checkout | `/porucivanje` | Client only |
| Order confirmation | `/porudzbina-primljena` | Client only |
| Order tracking | `/track-order` (Serbian alias `/pracenje-porudzbine`) | Client only, `noindex` |
| About | `/o-nama` | Prerendered |
| Contact | `/kontakt` | Prerendered |
| FAQ | `/cesta-pitanja` | Prerendered |
| Blog list / article | `/blog`, `/blog/:slug` | Prerendered |
| Privacy / Terms | `/politika-privatnosti`, `/uslovi-koriscenja` | Prerendered |

## P1.4 Products and prices

- Every product has at least one variant. The variant is the unit that is priced, stocked and sold.
- Regular price and discounted price are stored **per variant only**. The product-level "price" in listings is derived ("od 650 RSD" when variants differ). This avoids storing the same price in two places.
- The product keeps a base SKU for internal reference; each variant has its own unique SKU.
- A variant is purchasable when the product is active, the variant is active and stock is above zero.

## P1.5 Order status state machine

| From | Allowed next statuses |
|---|---|
| Received (Primljena) | Accepted, Rejected, Cancelled |
| Accepted (Prihvaćena) | Preparing, Cancelled |
| Preparing (U pripremi) | Shipped, Cancelled |
| Shipped (Poslata) | Delivered, DeliveryFailed |
| DeliveryFailed (Neuspešna isporuka) | Shipped (new attempt), Cancelled |
| Delivered, Cancelled, Rejected | none (final) |

Rules:

- Moving to **Shipped** requires a courier company; tracking number and shipment date are entered at the same step (tracking number may be added later).
- Every transition writes one row to the status history in the same database transaction as the status change and the email outbox entry.
- "Awaiting shipment" on the dashboard means status Preparing.

## P1.6 Stock policy

- Stock is **deducted when the order is created** (status Received), inside the order transaction.
- Stock is **returned automatically** on Rejected and on Cancelled when the order was never shipped.
- On **DeliveryFailed**, stock is not returned automatically. When such an order is then cancelled, the administrator chooses whether the returned goods go back on stock (food may come back unusable).
- Every change writes an inventory movement with the resulting quantity.

## P1.7 Emails per event

| Event | Email | Notes |
|---|---|---|
| Order created | Porudžbina primljena | Full order summary, tracking code and link |
| → Accepted | Porudžbina prihvaćena | |
| → Preparing | Porudžbina se priprema | |
| → Shipped | Porudžbina poslata | Courier and courier tracking number when entered |
| → Delivered | Porudžbina isporučena | |
| → Cancelled | Porudžbina otkazana | |
| → Rejected | Uses the cancellation email with the rejection message | Assumption, see open questions |
| → DeliveryFailed | No automatic email | Assumption; the administrator contacts the customer |

## P1.8 Revenue definitions (cash on delivery)

All periods are calculated in Serbian local time (Europe/Belgrade).

| Metric | Definition |
|---|---|
| Poručena vrednost (ordered value) | Sum of totals of orders created in the period, excluding Rejected and Cancelled |
| Vrednost isporučenih (delivered value) | Sum of totals of orders whose Delivered transition falls in the period |
| Procenjena naplata (estimated collected) | Equal to delivered value: cash on delivery is assumed collected at delivery |
| Stvarno naplaćeno (actual collected) | Sum of amounts the administrator has marked as received from the courier, by date received |
| Troškovi (expenses) | Sum of expenses entered by the administrator for the period |
| Procenjena dobit (estimated profit) | Delivered value minus expenses. It is only as complete as the expenses entered |
| Prosečna vrednost porudžbine | Ordered value divided by number of orders counted in it |

A newly created order never counts as collected revenue.

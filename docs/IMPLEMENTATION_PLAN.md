# ZLADOVCE – IMPLEMENTATION PLAN

> Part of the Zladovce master specification. Related documents:
> [PROJECT_SPECIFICATION](PROJECT_SPECIFICATION.md) · [ARCHITECTURE](ARCHITECTURE.md) · [DATABASE](DATABASE.md) · [API_SPECIFICATION](API_SPECIFICATION.md) · [AWS_INFRASTRUCTURE](AWS_INFRASTRUCTURE.md)

---

# 17. TESTING

Implement:

Backend:
- Domain unit tests.
- Application service tests.
- API integration tests.
- Validation tests.
- Order creation tests.
- Inventory concurrency tests.
- Tracking security tests.
- Email dispatch tests.
- Analytics tests.

Frontend:
- Component tests.
- Service tests.
- Form validation tests.
- Cart tests.
- Checkout tests.
- Admin authorization tests.
- Responsive UI tests.

End-to-end:
- Product purchase flow.
- Order management flow.
- Status update flow.
- Public tracking flow.
- Email notification flow.

Do not consider a feature complete merely because its UI exists.

All important functionality must be connected to real backend services.

---

# 19. IMPLEMENTATION WORKFLOW

IMPORTANT:

Do not attempt to build the entire platform in one response.

Work in controlled, verifiable phases.

## Phase 1 – Discovery and Architecture

First:
1. Analyze the existing website and references.
2. Produce functional requirements.
3. Define technical architecture.
4. Design database schema.
5. Define API endpoints.
6. Define Angular workspace structure.
7. Define AWS infrastructure.
8. Document security considerations.
9. Identify assumptions and open questions.
10. Produce implementation roadmap.

Deliver architecture documentation.

Do not start implementing the complete application before the architecture has been reviewed.

## Phase 2 – Foundation

Set up:
- Angular workspace.
- .NET solution.
- PostgreSQL.
- EF Core.
- Configuration.
- Logging.
- Swagger.
- Error handling.
- Authentication foundation.
- Initial AWS infrastructure.

## Phase 3 – Catalog and Administration

Implement:
- Products.
- Categories.
- Product variants.
- Images.
- Inventory.
- Admin interfaces.

## Phase 4 – Storefront

Implement:
- Homepage.
- Shop.
- Product pages.
- Search.
- Filters.
- Cart.
- Responsive navigation.

## Phase 5 – Checkout and Orders

Implement:
- Guest checkout.
- Cash on delivery.
- Shipping calculations.
- Order creation.
- Inventory consistency.
- Admin order management.

## Phase 6 – Tracking and Notifications

Implement:
- Secure tracking codes.
- Tracking page.
- Order history.
- Email notifications.
- Courier information.

## Phase 7 – Analytics and CMS

Implement:
- Dashboard.
- Reporting.
- Charts.
- Blog.
- CMS.
- Store settings.

## Phase 8 – Production Readiness

Implement:
- Full testing.
- Security review.
- Performance optimization.
- SEO.
- AWS deployment.
- Monitoring.
- Backups.

After every phase:

1. Summarize implementation.
2. List created and modified files.
3. Explain how to run the application.
4. Explain how to test the completed features.
5. Identify unfinished work.
6. Run relevant tests.
7. Ensure successful builds.
8. Update documentation.

Never claim that a feature is implemented if it is only partially implemented.

---

# 21. DEFINITION OF DONE

The project is complete only when:

1. A customer can browse products.
2. A customer can add products to a cart.
3. A customer can place an order without registering.
4. The system correctly validates inventory.
5. The system generates a unique order number.
6. The system generates a secure tracking code.
7. The customer receives a confirmation email.
8. An administrator can access the admin panel.
9. An administrator can manage products and images.
10. An administrator can manage inventory.
11. An administrator can manage orders.
12. An administrator can update order statuses.
13. An administrator can enter courier information.
14. A customer can track an order publicly.
15. Status updates trigger the correct email notifications.
16. The dashboard displays real data.
17. Revenue calculations are correct.
18. Blog and CMS functionality works.
19. The website is responsive.
20. Security and production deployment requirements are addressed.
21. Automated tests pass.
22. Documentation is complete.

The result must be a polished, reliable, production-ready Serbian e-commerce platform.

## FIRST ACTION

Start with Phase 1 – Discovery and Architecture.

Analyze the business requirements and website references.

Produce:
- System architecture.
- Database ERD.
- Database entity descriptions.
- API endpoint specification.
- Angular workspace architecture.
- AWS deployment diagram.
- Security model.
- Implementation roadmap.
- List of assumptions and unresolved business decisions.

Do not generate the entire application immediately.

Act as a senior technical partner throughout the project.

Your objective is to help build a real business platform, not just a prototype.

---
---

# PHASE 1 – ROADMAP, ASSUMPTIONS AND OPEN QUESTIONS

Everything below this line is Phase 1 output (design), not part of the original master specification.

## R1. Phase 1 deliverables

| Deliverable | Location |
|---|---|
| Analysis of existing site and reference, functional decisions | [PROJECT_SPECIFICATION.md](PROJECT_SPECIFICATION.md) – Phase 1 section |
| System architecture, Angular workspace, security model | [ARCHITECTURE.md](ARCHITECTURE.md) – Phase 1 section |
| Database ERD and entity descriptions | [DATABASE.md](DATABASE.md) – Phase 1 section |
| API endpoint specification | [API_SPECIFICATION.md](API_SPECIFICATION.md) – Endpoints |
| AWS deployment diagram, environments, costs | [AWS_INFRASTRUCTURE.md](AWS_INFRASTRUCTURE.md) – Phase 1 section |
| Roadmap, assumptions, open questions | This document |

**Status: Phase 1 documentation drafted, awaiting review. No application code has been written.**

## R2. Roadmap

Each phase ends with the checklist from §19 (summary, file list, run and test instructions, unfinished work, passing tests and builds, updated docs).

| Phase | Work | Done when |
|---|---|---|
| 2 – Foundation | .NET solution with 5 projects + 2 test projects; EF Core, first migration (identity, settings, audit); error handling, logging, Swagger, health endpoint; Docker Compose for local PostgreSQL; Angular workspace with both apps and libs, design tokens, Serbian locale; Cognito sign-in in admin; CDK app with network, database, API, Cognito, buckets for the development environment; CI on pull requests; rendering spike (prerender + webhook vs SSR on Amplify) | An administrator signs in to an empty admin shell against a deployed dev API; `GET /health` works through API Gateway; CI is green |
| 3 – Catalog and administration | Categories, products, variants, image upload pipeline, inventory with movements and low-stock list; admin screens for all of these; audit of changes | Admin creates a product with variants and photos, adjusts stock with a reason, and sees the movement history |
| 4 – Storefront | Layout, navigation, homepage, shop with filters, search, sorting and paging, category and product pages, cart with live quote, static content pages | A visitor finds a product on a phone, adds it to the cart, and the cart survives a reload and shows server-calculated prices |
| 5 – Checkout and orders | Checkout form and validation, order creation transaction, idempotency, order numbers, shipping rules, status state machine, admin order list and detail, shipment entry, stock return on cancel; concurrency tests | Two simultaneous orders for the last item produce one order and one clear refusal; a repeated request creates no duplicate; admin moves an order through every allowed status and is refused invalid ones |
| 6 – Tracking and notifications | Tracking codes, public tracking page, rate limiting, outbox worker, six Serbian email templates, email log and retry in admin, SES setup | Customer receives the confirmation, opens the tracking link, and sees each status change with its email; a forced SES failure is retried and the order is intact |
| 7 – Analytics and CMS | Dashboard metrics and charts, period filters, expenses, reports with CSV export, blog, CMS pages, FAQ, store settings, couriers, admin users, audit log screen, content rebuild webhook | Dashboard numbers match hand-calculated values on test data; a published blog post appears on the site and a draft does not |
| 8 – Production readiness | End-to-end tests, security review, accessibility and performance pass, SEO (structured data, sitemap, redirects from old URLs), staging and production environments, alarms, backup restore drill, runbook, cost review, launch checklist | All 22 Definition of Done items verified on staging and signed off by the owner |

Phases 3–7 each deliver backend, admin UI and tests together; a screen without a working backend does not count.

## R3. Assumptions

These are in effect unless the business says otherwise.

| # | Assumption |
|---|---|
| A1 | Prices are final consumer prices in RSD, whole dinars in practice, stored with two decimals |
| A2 | Prices are stored per variant; listings show the lowest variant price |
| A3 | Categories are a flat list |
| A4 | Stock is deducted when the order is placed, and returned on rejection or cancellation before shipping |
| A5 | After a failed delivery, the administrator decides whether goods go back on stock |
| A6 | A rejected order sends the cancellation email with the rejection message; a failed delivery sends no automatic email |
| A7 | One flat shipping price for all of Serbia, with an optional free-shipping threshold and minimum order amount |
| A8 | Two admin roles: Vlasnik (everything) and Urednik (daily operations) |
| A9 | "Customers" in the admin is a read-only list built from orders; there are no customer accounts |
| A10 | Newsletter in the MVP only collects consenting email addresses; no sending tool |
| A11 | Contact page shows store contact data and a simple form that emails the store |
| A12 | No analytics or advertising cookies in the MVP, so no cookie banner is needed |
| A13 | Testimonials section stays hidden until real testimonials are entered |
| A14 | Storefront is prerendered and rebuilt on content changes; live price and stock are loaded in the browser |
| A15 | Source control and CI/CD on GitHub |
| A16 | AWS region `eu-central-1`; no RDS Proxy and a small NAT instance at launch |
| A17 | Phone numbers are validated as Serbian numbers (`+381…` or `06…`), postal codes as five digits |
| A18 | The current site's data is not migrated; products are entered fresh in the admin |
| A19 | The word "organski" and similar claims are left out of all copy until certification is confirmed |

## R4. Open questions for the business

Ordered by how much they block.

### Needed before Phase 2 finishes

1. **Domain and DNS:** who controls DNS for `zladovce.com`, and can records be added (needed for email sending, certificates and the new site)?
2. **AWS account:** is there an existing AWS account, and who will own and pay for it? Is a separate account for production acceptable?
3. **Source control:** is GitHub acceptable, and under whose account or organisation?
4. **Monthly budget:** is roughly 30–40 USD per month for production acceptable, plus staging while it is running?

### Needed before Phases 3–5

5. **Catalog:** the real product list with sizes, prices, SKUs (or permission to generate them), descriptions and ingredients.
6. **Photography:** the current site uses stock photos. Are real product photos available or planned?
7. **"Organski" claim:** is the production certified organic? If not, which wording should be used ("domaći", "prirodni", "bez aditiva")?
8. **Fresh fruit:** is fresh fruit (Sveža borovnica) sold online and shipped by courier across Serbia, or only seasonally or locally?
9. **Shipping terms:** delivery price, free-delivery threshold, minimum order amount, estimated delivery text.
10. **Couriers:** which courier companies are used?
11. **Legal identity:** registered business name, PIB, matični broj, address, contact email and phone for the footer, legal pages and emails.
12. **Legal texts:** who provides the privacy policy and terms (returns and complaints rules for food, consumer withdrawal rights)? These need review by someone qualified in Serbian consumer law.
13. **Fiscal receipts:** how are fiscal receipts issued for cash-on-delivery sales today? The platform does not issue them; confirm this stays outside the system.
14. **VAT:** is the business in the PDV system, and must prices or emails show PDV?

### Needed before Phases 6–7

15. **Sender address:** which address should emails come from (e.g. `porudzbine@zladovce.com`) and where should replies go?
16. **Administrators:** how many people will use the admin and in which roles?
17. **Emails for rejected and failed-delivery orders:** keep assumption A6, or send dedicated emails?
18. **Collected payments:** will the administrator record money received from the courier per order (enables "actual collected"), or is the estimate enough?
19. **Expenses:** which expense categories are wanted (ambalaža, sirovine, dostava, ostalo)?
20. **Content:** family story text, blog topics, FAQ answers, social media links, real testimonials if any.
21. **Tracking URL:** keep `/track-order` as the address customers see, or use the Serbian alias `/pracenje-porudzbine` as the main one?

## R5. Risks

| Risk | Mitigation |
|---|---|
| SES production access is refused or delayed | Request early in Phase 2 with domain verified; outbox keeps emails until sending works |
| .NET cold starts make the first request slow | Measure in Phase 2; SnapStart / ReadyToRun; storefront pages are static so only API calls are affected |
| Prerender rebuild delay after content edits | Live price and stock; debounced webhook; SSR spike in Phase 2 as the alternative |
| Missing real content and photos delays launch | Questions 5, 6 and 20 raised now; placeholders are clearly marked and configurable |
| Non-technical administrator finds the admin hard | Review each admin screen with the owner on staging at the end of Phases 3, 5 and 7 |
| Legal texts and claims | Nothing invented; business supplies and approves all legal and product claims |

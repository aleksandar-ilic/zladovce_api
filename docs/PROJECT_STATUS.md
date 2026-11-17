# Zladovce – Where we stopped

Last updated: 2026-11-17

A short handover note: where the project stands, how to run it, and what comes next.

## Summary

- **Phases 1–7 are done**: built and tested locally, committed and pushed.
- **Phase 8 (Production Readiness) has not started.**
- **Nothing is deployed to AWS yet.** Everything is tested only on the local machine (Docker, local API, local Angular apps).

| Part | Repository | Last commit | Tests |
|---|---|---|---|
| Backend (.NET 10) | [zladovce_api](https://github.com/aleksandar-ilic/zladovce_api) `backend/` | `78f8f31` | 887, all passing |
| AWS CDK infrastructure | [zladovce_api](https://github.com/aleksandar-ilic/zladovce_api) `infrastructure/` | `78f8f31` | 134, all passing |
| Docs | [zladovce_api](https://github.com/aleksandar-ilic/zladovce_api) `docs/` | | |
| Frontend (storefront + admin) | [zladovce_web](https://github.com/aleksandar-ilic/zladovce_web), local folder `frontend/` | `dadc2b7` | 508, all passing |

Local layout: `C:\Git\zladovce_v2` is the `zladovce_api` repository. `frontend/` inside it is a separate git repository (`zladovce_web`) that the api repository ignores.

## Phases

| Phase | Status |
|---|---|
| 1 – Discovery and architecture | Done (design sections in every doc in `docs/`) |
| 2 – Foundation | Done |
| 3 – Catalog and administration | Done |
| 4 – Storefront | Done |
| 5 – Checkout and orders | Done |
| 6 – Tracking and notifications | Done |
| 7 – Analytics and CMS | Done |
| 8 – Production readiness | **Not started** |

## Next: Phase 8 – Production readiness

Can be done **without AWS access** (planned to start first):
1. End-to-end tests of the main flows: browse → cart → checkout → email → tracking, and the admin order flow up to Delivered.
2. Security review: headers/CSP, auth, rate limits, input handling, dependencies.
3. Performance and accessibility checks (Lighthouse, keyboard navigation, contrast).
4. Redirects from the old site's addresses (e.g. `/proizvod/{id}`) to the new product pages.

Needs **AWS access**:
5. First deployment of the dev environment, then staging, then production.
6. Monitoring and alarms (email notifications), budget alert.
7. Backups and a restore drill on staging.
8. Production go-live checklist.

## Needed from the owner before deployment

- [ ] AWS deploy access (GitHub Actions through OIDC is the recommended setup; no long-lived keys).
- [ ] Where `zladovce.com` is registered and who manages its DNS (for the subdomains and the SES email records).
- [ ] Email address for alarms and the budget warning.
- [ ] Real data: products, package sizes, photos, shipping prices, couriers, company details, legal texts (privacy, terms, delivery).
- [ ] Answers to the open business questions in [IMPLEMENTATION_PLAN](IMPLEMENTATION_PLAN.md#r4-open-questions-for-the-business).

Manual steps on the first deployment are listed in `infrastructure/README.md`: SES DNS records and production access, a Lambda concurrency quota of at least 136, the tracking key, the first Owner account (`aws cognito-idp admin-create-user` + `admin-add-user-to-group Owner`), and the Amplify build hook stored in the secret `zladovce/<env>/site-rebuild-webhook-url`.

## Decisions taken

- **One AWS account** for dev, staging and production, separated by stack names (`Zladovce-<Env>-*`), tags and IAM. Region `eu-central-1`.
- **One domain** `zladovce.com` with subdomains: `www`/root = storefront, `admin.` = admin, `api.` = API, `media.` = images; staging uses `staging.`, `admin-staging.`, `api-staging.`. Dev uses AWS-generated URLs.
- **First iteration uses local test data**: 5 test products from the current site (`backend/scripts/seed-dev-catalog.ps1`).
- Infrastructure and docs live in the api repository.
- No RDS Proxy at launch; a NAT instance instead of a NAT Gateway (cost). Estimated production cost is about 30–40 USD/month (see [AWS_INFRASTRUCTURE](AWS_INFRASTRUCTURE.md#w10-estimated-monthly-cost-production)).

## How to run locally

Needs Docker, the .NET 10 SDK and Node 24. Port 4200 is used by another app on this machine, so the storefront runs on **4210**.

```powershell
# 1. Database, MinIO (photos) and Mailpit (emails)
cd C:\Git\zladovce_v2\backend
docker compose up -d

# 2. API -> http://localhost:5080 (Swagger at /swagger)
$env:Storefront__BaseUrl = "http://localhost:4210"
dotnet run --project src/Zladovce.Api

# 3. Test products (once, in another terminal)
powershell -File scripts/seed-dev-catalog.ps1

# 4. Storefront -> http://localhost:4210
cd C:\Git\zladovce_v2\frontend
npx ng serve storefront --port 4210

# 5. Admin -> http://localhost:4201 (signed in automatically as Owner)
npm run start:admin
```

| URL | What |
|---|---|
| http://localhost:4210 | Storefront |
| http://localhost:4201 | Admin |
| http://localhost:5080/swagger | API |
| http://localhost:8025 | Mailpit: every email the shop sends |
| http://localhost:9001 | MinIO console (user `zladovce`, password `zladovce_dev_minio`) |

First time in the admin: turn shipping on and set the store contact email in **Podešavanja**, and add at least one courier (needed to mark an order as shipped).

Stop: Ctrl+C in each terminal, then `docker compose down` in `backend`.

Run the tests:

```powershell
cd C:\Git\zladovce_v2\backend; dotnet test            # needs Docker
cd C:\Git\zladovce_v2\infrastructure; npm test
cd C:\Git\zladovce_v2\frontend; npm run lint; npm run test:ci
```

## Known small issues (backlog)

- When the contact form is not configured, the 409 message mentions a phone number even if none is set.
- The "Dead" email state takes about 15 hours of retries to reach, so it is covered by tests only, not by a live check.
- The API's default `Storefront:BaseUrl` in Development is `http://localhost:4200`; override it to 4210 as above.
- Local limits: checkout allows 5 orders per 10 minutes per computer. Cognito sign-in and administrator invitations work only on AWS.
- Test products have no photos; their descriptions are marked as test data.

## Resuming

To continue, tell Claude Code (in `C:\Git\zladovce_v2`): *"Read docs/PROJECT_STATUS.md and start Phase 8 with the parts that don't need AWS."*

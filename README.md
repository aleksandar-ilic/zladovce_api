# Zladovce

Production-ready e-commerce platform for Zladovce, a family-owned business from southern Serbia selling homemade and natural products (jams, juices, vinegar, compotes, fruit products).

- Market: Serbia · Currency: RSD · Language: Serbian Latin
- Payment: cash on delivery (pouzećem) · Guest checkout, no customer accounts in MVP
- Stack: Angular · .NET 10 (ASP.NET Core, EF Core) · PostgreSQL · AWS (Amplify, API Gateway, Lambda, RDS, S3, CloudFront, SES, Cognito)

## Repositories

| Repository | Contents |
|---|---|
| [zladovce_api](https://github.com/aleksandar-ilic/zladovce_api) (this one) | `backend/` .NET modular monolith, `infrastructure/` AWS CDK, `docs/`, CI and deploy workflows |
| [zladovce_web](https://github.com/aleksandar-ilic/zladovce_web) | Angular workspace (storefront, admin, shared libs) with its own CI |

```
zladovce_api/
├── docs/            Project documentation
├── backend/         .NET modular monolith
├── infrastructure/  Infrastructure as Code (AWS CDK, TypeScript)
├── .github/         CI (backend + infrastructure) and deploy workflows
└── README.md
```

For local work, clone `zladovce_web` either next to this repository or into a `frontend/` folder inside it (ignored by this repository). The frontend's `npm run generate:models` finds the OpenAPI document in both layouts.

## Documentation

**Where we stopped and what comes next: [PROJECT_STATUS](docs/PROJECT_STATUS.md).**

| Document | Contents (master spec sections) |
|---|---|
| [PROJECT_SPECIFICATION](docs/PROJECT_SPECIFICATION.md) | Business context, design system, storefront, products & inventory, cart & checkout, orders, tracking, shipping, emails, admin, blog & CMS, SEO, engineering rules (§1, §4–13, §16, §20) |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | Technology stack, modular monolith, frontend workspace, authentication & security (§2, §3, §15) |
| [DATABASE](docs/DATABASE.md) | PostgreSQL schema requirements and data rules (§14) |
| [API_SPECIFICATION](docs/API_SPECIFICATION.md) | API requirements collected from the spec; endpoints to be defined in Phase 1 |
| [AWS_INFRASTRUCTURE](docs/AWS_INFRASTRUCTURE.md) | AWS services, environments, deployment & operations (§2 AWS, §18) |
| [IMPLEMENTATION_PLAN](docs/IMPLEMENTATION_PLAN.md) | Testing, phased workflow, definition of done, first action (§17, §19, §21) |

## Current status

Phase 1 – Discovery and Architecture: documentation drafted, awaiting review. Each document holds the original requirements first, then a "PHASE 1" design section.

Phase 2 – Foundation: built and tested locally (backend 179 tests, frontend 198 tests, infrastructure 81 tests, all passing as of 2026-11-14). Nothing is deployed to AWS; Cognito sign-in, RDS IAM authentication, Lambda hosting and the GitHub workflows are untested against real services.

Phases 3–7 – Catalog, Storefront, Checkout and Orders, Tracking and Notifications, Analytics and CMS: built and tested locally (as of 2026-11-17: backend 887 tests, frontend 508 tests, infrastructure 134 tests, all passing). Nothing is deployed to AWS.

Phase 8 – Production Readiness: not started.

The business questions in [IMPLEMENTATION_PLAN](docs/IMPLEMENTATION_PLAN.md#r4-open-questions-for-the-business) are still open; work proceeds on the assumptions in section R3.

## Running locally

| Part | Folder | Start | Details |
|---|---|---|---|
| Database | `backend/` | `docker compose up -d` (PostgreSQL on port 5433) | [backend/README.md](backend/README.md) |
| API | `backend/` | `dotnet run --project src/Zladovce.Api` → http://localhost:5080 | [backend/README.md](backend/README.md) |
| Storefront | `zladovce_web` | `npm run start:storefront` → http://localhost:4200 | zladovce_web `README.md` |
| Admin | `zladovce_web` | `npm run start:admin` → http://localhost:4201 | zladovce_web `README.md` |
| Infrastructure | `infrastructure/` | `npx cdk synth -c env=dev` (offline) | [infrastructure/README.md](infrastructure/README.md) |

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

## Layout

```
backend/
├── Zladovce.sln
├── Directory.Build.props        net10.0, nullable, warnings as errors
├── Directory.Packages.props     central package versions
├── docker-compose.yml           local PostgreSQL 17 (host port 5433) and MinIO (9000/9001)
├── dotnet-tools.json            local tool: dotnet-ef
├── openapi/zladovce-v1.json     committed OpenAPI document (source for frontend models)
├── scripts/generate-openapi.ps1
├── src/
│   ├── Zladovce.Domain/          entities and rules, no dependencies
│   ├── Zladovce.Application/     services, DTOs, validators, ports
│   ├── Zladovce.Infrastructure/  EF Core, migrations, database connection, Cognito profile lookup
│   ├── Zladovce.Api/             endpoints, auth, error handling, logging, OpenAPI, Lambda HTTP entry
│   └── Zladovce.Lambda/          non-HTTP handlers (MigrationRunner, ImageProcessorFunction)
└── tests/
    ├── Zladovce.UnitTests/
    └── Zladovce.IntegrationTests/  real PostgreSQL through Testcontainers
```

Dependency rule: `Domain` ← `Application` ← `Infrastructure` ← `Api` / `Lambda`. Each project is organised by
module folder (`Identity`, `StoreSettings`, `Audit`, `Categories`, `Catalog`, `Inventory`, `Media`, `Common`).

## Prerequisites

- .NET SDK that can build `net10.0` (.NET 10 SDK or newer) and the .NET 10 runtime
- Docker (local database and integration tests)

## Start PostgreSQL and MinIO

```powershell
cd backend
docker compose up -d        # PostgreSQL 17 on localhost:5433, db/user "zladovce", password "zladovce_dev"
                            # MinIO (S3-compatible) API http://localhost:9000, console http://localhost:9001
docker compose down         # stop (add -v to delete the data volumes)
```

These credentials are for local development only.

MinIO stands in for the AWS media bucket. The compose file starts the server (user `zladovce`, password
`zladovce_dev_minio`), creates the bucket `zladovce-media-dev` and makes `media/*` publicly readable (like
CloudFront on AWS); `uploads/*` stays private. Browser uploads are allowed from `http://localhost:4201` (the admin
application) through `MINIO_API_CORS_ALLOW_ORIGIN` (MinIO has no per-bucket CORS). The images are Chainguard builds
of the open-source MinIO server and client, because MinIO no longer publishes its own container images.

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
`detail`, `traceId`, and an `errors` map (camelCase field path → messages) for validation failures:

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
`INTERNAL_ERROR`, `SERVICE_UNAVAILABLE`. Application code throws `RequestValidationException`,
`NotFoundException`, `ConflictException` or `ForbiddenException` (`Zladovce.Application.Common.Errors`);
one middleware turns them into responses.

Every response carries `X-Correlation-Id` (the caller's value when it is well formed, otherwise generated);
the same id is on every log line of the request. Logs are JSON on stdout. Request logs contain the path
only, never the query string, bodies, emails or tokens.

Request DTOs are validated automatically: any endpoint argument under `/api/v1` whose type has a registered
FluentValidation `IValidator<T>` is validated before the handler runs. Put validators in
`Zladovce.Application`; they are registered by assembly scan.

## Endpoints (Phase 3)

Public (no authentication) under `/api/v1/catalog`: `GET categories`, `GET categories/{slug}`, `GET products`
(`category`, `q`, `minPrice`, `maxPrice`, `inStock`, `featured`, `sort` = `newest|priceAsc|priceDesc|name|bestselling`,
`page`, `pageSize`), `GET products/{slug}`, `GET products/{slug}/related?limit=`, `GET availability?variantIds=`.

Admin (`AdminAny`, i.e. Owner or Staff) under `/api/v1/admin`: `categories` (list, create, read, update, archive,
`PUT categories/order`), `products` (list, create, read, update with `version`, `POST {id}/archive`,
`POST {id}/restore`), `products/{id}/variants[/{variantId}]`, `products/{id}/images[/order|/{imageId}]`,
`POST media/upload-url`, `inventory` (list, `POST {variantId}/adjustments`, `PUT {variantId}/threshold`,
`GET {variantId}/movements`), `GET dashboard/low-stock`.

The exact contract (fields, query parameters, status codes, error codes in each operation's description) is
`openapi/zladovce-v1.json`.

Catalog rules:

- Prices exist on variants only (`numeric(12,2)`, RSD). The effective price is the discount price when set, else the
  regular price (`Pricing` in the domain). A product's listing price is the lowest effective price of its active
  variants; `hasPriceRange` tells the shop to show "od …".
- The shop shows a product only when it is active, not archived, in an active (and not archived) category and has
  at least one active variant. Exact stock is never published: variants expose `inStock` and
  `maxOrderQuantity = min(stock, Catalog:MaxOrderQuantity)` (default 20).
- Sort `bestselling` is accepted but ordered like `newest` until orders exist (Phase 5). There is no
  `/catalog/bestsellers` endpoint yet.
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
- `inventory_movements.order_id` is a plain nullable `uuid` **without a foreign key** for now; the orders table and the
  foreign key arrive in Phase 5.

Audit: an EF Core `SaveChanges` interceptor writes one `audit_logs` row per created/updated/archived/restored/
deleted category, product, variant, product image and inventory record (`{Entity}.{Created|Updated|Archived|Restored|Deleted}`,
changed fields as `{ "field": { "old": …, "new": … } }`, administrator id, IP), in the same transaction. Stock
quantity changes bypass the change tracker and are audited explicitly (`Inventory.Adjusted`).

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

Deleting an image (or replacing a category image) deletes its renditions after the database change is saved.

Image library: **Magick.NET** (`Magick.NET-Q8-AnyCPU` 15.1.0, Apache-2.0; ImageMagick itself is under the
ImageMagick licence, Apache-2.0 style). Free for commercial use; the package ships native binaries for `win-x64`,
`linux-x64`, `linux-arm64` and others.

## Lambda handlers

| Handler | Purpose | Trigger / input |
|---|---|---|
| `Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync` | `{}` or `{ "action": "migrate" }`: apply migrations as `Database__User` (migrator). `{ "action": "bootstrap" }`: create the roles (below) | Pipeline |
| `Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync` | Process uploads into renditions. No database access | S3 `s3:ObjectCreated:*` on the media bucket, prefix `uploads/` |

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

Run it after changing endpoints or DTOs and commit the file.

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
| `Media:ReadyWaitSeconds` | How long attach waits for the image Lambda (default 8) |
| `Media:UploadUrlLifetimeMinutes` | Lifetime of a pre-signed upload (default 10) |
| `Catalog:MaxOrderQuantity` | Cap of `maxOrderQuantity` in the shop (default 20) |

Nothing the API needs depends on the environment being called `Development`: deployed environments (`Dev`,
`Staging`, `Production`) only differ in configuration; Swagger, start-up migration and the development
authentication exist only in `Development` (integration-tested for `Dev`, `Staging` and `Production`).

No secrets belong in these files. In IAM mode the API holds no database password: for each new connection
it signs a short-lived token locally with the Lambda role (`RDSAuthTokenGenerator`), reused for 10 minutes.

The application refuses to start when:

- `Auth:DevBypass` is `true` outside the Development environment, or inside AWS Lambda;
- `Auth:DevBypass` is off and the Cognito settings are incomplete;
- `Cors:AllowedOrigins` contains anything that is not an exact origin.

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

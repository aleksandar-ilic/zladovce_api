# Zladovce – infrastructure (AWS CDK)

AWS CDK v2 application (TypeScript) for the Zladovce platform. Design reference:
[`docs/AWS_INFRASTRUCTURE.md`](../docs/AWS_INFRASTRUCTURE.md) (W1–W10) and
[`docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md) (A1, A4, A6).

> **Status:** this code has been synthesised and unit-tested offline only. It has **never been
> deployed** – there is no AWS account yet. Expect to fix details on the first real deployment.

## Stacks

One set of stacks per environment, named `Zladovce-<Env>-<Part>` (`Dev`, `Staging`, `Prod`):

| Stack | Contents |
|---|---|
| `Network` | VPC (2 AZs, public + private subnets), NAT instance (`t4g.nano`) or NAT gateway, S3 gateway endpoint, all security groups |
| `Database` | RDS PostgreSQL 17 (`db.t4g.micro`, 20 GB gp3, encrypted, private, single-AZ, IAM authentication, RDS-managed master secret), optional RDS Proxy |
| `Auth` | Cognito user pool for administrators (no self sign-up, groups `Owner` / `Staff`, optional TOTP), public SPA client |
| `Media` | Private S3 bucket (`uploads/`, `media/`) + CloudFront with Origin Access Control serving `media/*` only; image processor Lambda (S3 trigger on `uploads/`, outside the VPC) with its SQS dead-letter queue |
| `Migrations` | Migration runner Lambda (pipeline only, not behind API Gateway) |
| `Api` | API Lambda (ASP.NET Core, .NET 10, arm64) with alias `live`, HTTP API with JWT authorizer on `/api/v1/admin/*` |
| `Observability` | SNS alarm topic, CloudWatch alarms (API 5xx / p95 latency, Lambda errors / throttles, image processor errors / dead-lettered uploads, RDS CPU / storage / connections, NAT status check) |
| `Frontend` | Amplify Hosting apps (storefront, admin) built from the separate frontend repository. Only when `amplify.enabled` is `true` |

Dependencies point one way only (`Network ← Database ← Migrations / Api ← Observability`,
`Auth, Media ← Api ← Frontend`, `Media ← Observability`), so there are no cyclic cross-stack references.

**Why the image processor is in the `Media` stack.** An S3 notification to a Lambda function needs references in
both directions: the bucket's notification configuration names the function, and the function's invoke permission
names the bucket (as `SourceArn`). With the bucket in `Media` and the function in another stack this is a cyclic
cross-stack dependency. With bucket, function, permission, notification (a CDK-owned `Custom::S3BucketNotifications`
resource, which avoids the bucket ↔ permission cycle inside the stack) and dead-letter queue in `Media`, that stack
has no cross-stack references at all; `Observability` reads the function and the queue from it.

```
infrastructure/
├── bin/zladovce.ts              entry point (selects the environment)
├── lib/config.ts                ALL environment configuration (typed)
├── lib/zladovce-environment.ts  wires the stacks together, tags
├── lib/stacks/*.ts              one file per stack
├── lib/constructs/dotnet-function.ts   .NET Lambda with its own role and log group
├── lib/artifacts.ts             Lambda package resolution / placeholder fallback
├── lib/nag-suppressions.ts      cdk-nag acknowledgments with reasons
├── amplify/*.yml                Amplify build specs (read by the Frontend stack)
├── assets/placeholder/          stand-in Lambda package for offline synth
└── test/*.test.ts               Jest unit tests (+ cdk-nag)
```

## Prerequisites

- Node.js 22 or newer (developed with 24), npm.
- Nothing else for `synth` and `test`: **no AWS credentials, no Docker, no backend build**.
- For a deployment: AWS credentials for the target account, a bootstrapped environment and the
  published backend packages (see below).

```
npm ci
```

## Commands

| Command | What it does |
|---|---|
| `npm run build` | Type-check (`tsc --noEmit`) |
| `npm test` | Unit tests, including the cdk-nag AwsSolutions pack for every environment |
| `npx cdk synth -c env=dev` | Synthesise one environment (`dev`, `staging`, `prod`) into `cdk.out/` |
| `npm run synth:all` | Synthesise all three environments |
| `npm run nag` | Synthesise dev with cdk-nag enabled (`-c nag=true` works with any environment) |
| `npx cdk diff --all -c env=staging -c requireArtifacts=true` | Compare with what is deployed (needs credentials) |
| `npx cdk deploy --all -c env=staging -c requireArtifacts=true` | Deploy (needs credentials) |

Without `-c env=…` the environment is `dev`.

### Lambda packages and the placeholder

The Lambda code comes from two publish directories (paths relative to `infrastructure/`):

| Config key / context | Default | Handlers |
|---|---|---|
| `apiAssetPath` | `../backend/artifacts/api` | `Zladovce.Api` (API function) |
| `lambdaAssetPath` | `../backend/artifacts/lambda` | `Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync` (migration runner), `Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync` (image processor) |

`lambdaAssetPath` was called `migrationAssetPath` before the image processor shared the package; `-c migrationAssetPath=…`
is still accepted.

Produce them with:

```
dotnet publish backend/src/Zladovce.Api    -c Release -r linux-arm64 --self-contained false -o backend/artifacts/api
dotnet publish backend/src/Zladovce.Lambda -c Release -r linux-arm64 --self-contained false -o backend/artifacts/lambda
```

If a directory is missing, synthesis uses `assets/placeholder/` instead and prints a loud warning, so
that `synth` and the tests work on any machine. **A template with placeholder code must never be
deployed.** Always deploy with `-c requireArtifacts=true`, which turns a missing package into an error.

## Configuration per environment

Everything is in [`lib/config.ts`](lib/config.ts). Nothing there is a secret.

| Setting | dev | staging | prod |
|---|---|---|---|
| `account` | not set (uses the credentials' account) | not set | not set |
| `region` / AZs | `eu-central-1` / `a`, `b` | same | same |
| `network.natMode` | `instance` | `instance` | `instance` |
| `network.flowLogs` | off | off | on (rejected traffic) |
| `database.backupRetentionDays` | 1 | 1 | 7 |
| `database.deletionProtection` | no | no | yes |
| Database on stack deletion | deleted | final snapshot | final snapshot |
| `database.useRdsProxy` | no | no | no |
| Cognito pool / media bucket on stack deletion | deleted | deleted (bucket must be empty) | retained |
| Log retention | 30 days | 30 days | 90 days |
| API throttling (rate / burst) | 20 / 40 | 50 / 100 | 50 / 100 |
| `api.reservedConcurrency` | 10 | 10 | 10 |
| `ASPNETCORE_ENVIRONMENT` | `Dev` | `Staging` | `Production` |
| CORS origins | `http://localhost:4200`, `http://localhost:4201` | `https://staging.zladovce.com`, `https://admin-staging.zladovce.com` | `https://zladovce.com`, `https://www.zladovce.com`, `https://admin.zladovce.com` |
| Custom domains (`domains.api`, `domains.media`) | none | none (commented placeholders) | none (commented placeholders) |
| `amplify.enabled` | no | no | no |
| `amplify.repository` / branch | `https://github.com/aleksandar-ilic/zladovce_web` / `develop` | same / `main` | same / `production` |
| `api.mediaReadyWaitSeconds` (`Media__ReadyWaitSeconds`) | 8 | 8 | 8 |
| `api.maxOrderQuantity` (`Catalog__MaxOrderQuantity`) | not set (backend default 20) | not set | not set |
| Image processor (`media.processor*`) | 1024 MB, 60 s, 2 retries, DLQ kept 14 days | same | same |

Notes:

- **`account`** is deliberately unset: stacks are environment-agnostic and deploy to whatever account
  the credentials belong to. Set it per environment once the account decision is made.
- **`ASPNETCORE_ENVIRONMENT` is never `Development` in AWS.** The backend's
  `appsettings.Development.json` points at a local database and enables the authentication bypass
  (the API refuses to start in Lambda with the bypass on).
- **Custom domains** are optional. Without them the API uses the generated `execute-api` URL and media
  the generated CloudFront domain. To add one, create/validate an ACM certificate yourself (API: in
  the stack region; media: in `us-east-1`), put the domain and certificate ARN in `domains`, deploy,
  then create the DNS record (targets are stack outputs). The app does not create certificates or DNS
  records – where DNS lives is still an open question.
- **CORS** is handled by the ASP.NET Core application from `Cors__AllowedOrigins__N`; the gateway has
  no CORS configuration. `OPTIONS /api/v1/admin/{proxy+}` is routed without the authorizer so that
  preflight requests reach the application.
- **Reserved concurrency:** a brand-new AWS account often has a Lambda concurrency quota of 10, in
  which case any reservation is rejected at deploy time. Request a quota increase, or set
  `api.reservedConcurrency` to `undefined` temporarily. The image processor has no reservation.
- **Timeouts.** The HTTP API gives up after 30 s (hard limit of the integration). `validateConfig` refuses an
  `api.timeoutSeconds` above that, and an `api.mediaReadyWaitSeconds` that is not below both `api.timeoutSeconds`
  (15) and 30 s – the attach endpoints must answer `409 IMAGE_NOT_READY` themselves, before Lambda or the gateway
  cut them off. (The backend additionally clamps the wait to 0–25 s.)

Command-line overrides: `-c account=…`, `-c region=…`, `-c natMode=instance|gateway`,
`-c useRdsProxy=true|false`, `-c apiAssetPath=…`, `-c lambdaAssetPath=…`, `-c alarmEmail=…`,
`-c requireArtifacts=true`, `-c nag=true`. Prefer editing `lib/config.ts` for anything permanent, so
that every deployment of an environment uses the same values.

### What the Lambda functions receive

API function (`zladovce-<env>-api`):

```
ASPNETCORE_ENVIRONMENT
Database__Host  Database__Port  Database__Name  Database__User (zladovce_app)
Database__UseIamAuth=true  Database__Region  Database__MaxPoolSize=2
Auth__Cognito__Region  Auth__Cognito__UserPoolId  Auth__Cognito__ClientId
Media__BucketName  Media__BaseUrl  Media__Region
Media__ProcessInline=false  Media__ReadyWaitSeconds (8)
Catalog__MaxOrderQuantity (only when api.maxOrderQuantity is set)
Cors__AllowedOrigins__0 … N
```

`Database__User` is `zladovce_app`, the data-only role created by the bootstrap (SELECT/INSERT/UPDATE/DELETE, no DDL);
the API role may `rds-db:connect` as that user only.

Image processor (`zladovce-<env>-image-processor`, not in the VPC): `Media__BucketName`, `Media__Region`. No database
settings and no database permission.

Migration function (`zladovce-<env>-migrations`): `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`,
`Database__*` as above with `Database__User=zladovce_migrator` (always the instance endpoint, never the
proxy), `Database__AppUser`, and `Database__MasterSecretArn` when
`database.migrationRunnerCanReadMasterSecret` is on.

IAM:

| Role | Permissions |
|---|---|
| API | `rds-db:connect` as `zladovce_app`; `s3:PutObject` on `uploads/*` (the pre-signed POST is signed with this role, so this is what authorises the browser upload); `s3:GetObject` + `s3:DeleteObject` on `media/*` (attach reads `manifest.json`, deleting/replacing/archiving removes renditions); `s3:ListBucket` on the media bucket |
| Image processor | `s3:GetObject` + `s3:DeleteObject` on `uploads/*`; `s3:PutObject` + `s3:GetObject` on `media/*`; `s3:ListBucket` on the media bucket; `sqs:SendMessage` (+ `GetQueueAttributes`, `GetQueueUrl`, added by CDK) on its dead-letter queue. No VPC, database or secrets permissions |
| Migration runner | `rds-db:connect` as `zladovce_migrator`; `secretsmanager:GetSecretValue` on the RDS-managed master secret while `database.migrationRunnerCanReadMasterSecret` is on |

`s3:ListBucket` is what makes S3 answer **404 instead of 403** for a missing object (the manifest of an image that is
still being processed). It is granted on the one media bucket **without** an `s3:prefix` condition: S3 evaluates this
permission for `GetObject`/`HeadObject` without a prefix in the request context, so a prefix condition would never
match and missing objects would be 403 again. It lets the two roles list keys of this bucket (upload keys and image
folders – no personal data); it is not a wildcard resource, so cdk-nag does not flag it.

Media bucket CORS: `POST` only (pre-signed multipart form upload), from `cors.adminOrigins` only, `AllowedHeaders: *`,
`ETag` exposed. Lifecycle: objects under `uploads/` expire after `media.uploadsExpirationDays` (1 day) – leftovers of
uploads that were never processed; noncurrent versions expire after 30 days.

Image processor failures: asynchronous S3 invocations are retried twice by Lambda (`media.processorRetryAttempts`),
events older than one hour are dropped from the retry queue, and an event that still fails goes to the SQS
dead-letter queue `zladovce-<env>-image-processor-dlq` (on-failure destination, SSE-SQS, TLS only, 14 days). Alarms:
`ImageProcessorErrors` (Lambda errors ≥ `observability.lambdaErrorThreshold` in 5 minutes) and
`ImageProcessorDeadLetters` (any visible message). The administrator sees `IMAGE_NOT_READY` for such an upload and can
upload the file again; inspect the message (`ImageProcessorDeadLetterQueueUrl` output), fix the cause, then delete it.

## Cost switches

| Switch | Default | Effect | Approx. cost |
|---|---|---|---|
| `network.natMode` | `instance` | `instance`: one `t4g.nano` NAT instance. `gateway`: managed NAT gateway | 7–8 vs 38–40 USD / month |
| `database.useRdsProxy` | `false` | Adds an RDS Proxy (end-to-end IAM authentication) and points the API at it; the migration runner keeps using the instance | +22 USD / month |
| `network.flowLogs` | prod only | VPC flow logs to CloudWatch | log volume |
| `media.autoDeleteObjects` | dev only | Empties the bucket when the stack is deleted (adds a CDK custom resource) | – |

When to change them is described in `docs/AWS_INFRASTRUCTURE.md` W4 (connection alarm firing, or
concurrency above ~30 → proxy) and W5 (NAT).

NAT instance notes: it runs Amazon Linux 2023 from the "latest" public SSM parameter, so a deployment
after a new AMI release **replaces the instance** (a short outbound outage; e-mails wait in the outbox).
It only forwards TCP 443 from the VPC. Its public IP is not static.

## First-time deploy checklist

Nothing below has been executed yet.

1. **Account and region.** Decide the account(s) (a separate production account is recommended) and
   set `account` in `lib/config.ts`.
2. **Bootstrap** each account/region once: `npx cdk bootstrap aws://<account>/eu-central-1`.
3. **Lambda quota.** Check the account's Lambda concurrency quota (see "Reserved concurrency" above).
4. **Publish the backend** packages (commands above).
5. **Deploy** – in this order, so that migrations run before the new API version is live:
   1. `npx cdk deploy Zladovce-<Env>-Migrations -c env=<env> -c requireArtifacts=true` (also deploys
      `Network` and `Database`).
   2. **Create the database roles.** The database is private and has only the master user. Two roles
      must exist before anything can connect with IAM authentication: `zladovce_migrator` (may change
      schema) and `zladovce_app` (data only), both with `GRANT rds_iam`. The migration runner creates
      them with the RDS-managed master secret (`Database__MasterSecretArn`; it runs in the private
      subnets and reaches Secrets Manager through the NAT over HTTPS):
      `aws lambda invoke --function-name zladovce-<env>-migrations --payload '{"action":"bootstrap"}' --cli-binary-format raw-in-base64-out bootstrap.json`.
      The action is idempotent. `deploy.yml` runs it on every deployment before `migrate` while the
      Migrations stack output `MigrationBootstrapEnabled` is `true` (= `database.migrationRunnerCanReadMasterSecret`,
      default on). Once the roles exist you may set that switch to `false` and redeploy: the master-secret
      permission disappears and the pipeline skips the bootstrap.
   3. Invoke the migration function: `aws lambda invoke --function-name zladovce-<env>-migrations --payload '{"action":"migrate"}' --cli-binary-format raw-in-base64-out out.json` (`{}` means the same).
   4. `npx cdk deploy --all -c env=<env> -c requireArtifacts=true`.
   5. `curl <ApiUrl>/health`.
6. **First administrator.** Create the first user in the Cognito pool (console or CLI) and add it to
   the `Owner` group; self sign-up is disabled.
7. **Alarm e-mail.** Set `observability.alarmEmail` and confirm the subscription e-mail from SNS.
8. **Frontend values.** The frontend needs only public values, all stack outputs: `ApiUrl`,
   `UserPoolId`, `UserPoolClientId`, region, `MediaBaseUrl`.
9. **Amplify (later).** The frontend is its own repository,
   [`zladovce_web`](https://github.com/aleksandar-ilic/zladovce_web), with the Angular workspace at the
   repository **root** (`amplify.repository` defaults to it; the build specs in `amplify/` are plain
   single-application specs without `appRoot`, building `npm run build:storefront` → `dist/storefront/browser`
   and `npm run build:admin` → `dist/admin/browser`; no `AMPLIFY_MONOREPO_APP_ROOT`). Store a GitHub access token
   as a plain-text secret in Secrets Manager, set `amplify.enabled`, `amplify.accessTokenSecretName` and check
   `amplify.branch`, deploy, then add the generated Amplify URLs (stack outputs) to `cors.storefrontOrigins` /
   `cors.adminOrigins` and deploy again. Custom domains for the Amplify apps are not configured by
   this code – associate them in the Amplify console. The apps receive `ZLADOVCE_*` environment
   variables at build time; the frontend build does not read them yet. `deploy.yml` does not build the
   frontend.
10. **Still to come (later phases):** SES domain identity (DKIM, SPF, DMARC, custom MAIL FROM,
    production access request) and Cognito e-mail through SES; custom domains and DNS records;
    AWS Budget alert.
11. **CI/CD.** `.github/workflows/deploy.yml` needs a GitHub OIDC provider and deploy role in the
    account, and GitHub environments `staging` / `production` (with required reviewers) holding the
    variable `AWS_DEPLOY_ROLE_ARN`. The workflow is **untested**.

## Rollback

| What | How |
|---|---|
| API code | The HTTP API invokes the Lambda alias `live`. Point it back at the previous version: `aws lambda update-alias --function-name zladovce-<env>-api --name live --function-version <previous>`; then redeploy the previous commit so that CloudFormation and reality agree again. (CDK keeps old versions only until the next deployment replaces them – redeploying the previous commit is the reliable path.) |
| Infrastructure | Check out the previous commit and deploy it. Review `cdk diff` first |
| Database schema | Migrations are forward-only and backward-compatible, so a code rollback needs no schema rollback |
| Database data | Point-in-time recovery within the backup retention (7 days in production) restores to a **new** instance. Take a manual snapshot before each production release |
| Images | S3 versioning; noncurrent versions are kept for 30 days |
| Failed deployment | CloudFormation rolls the failing stack back automatically; stacks deployed earlier in the same run stay on the new version |

Production safety nets: stack termination protection, RDS deletion protection and final snapshot,
retained Cognito pool, media bucket and log groups.

Cross-stack references use classic CloudFormation exports (`@aws-cdk/core:defaultCrossStackReferences`
is set to `strong` in `cdk.json`). An exported value cannot change while another stack imports it; if
a deployment fails with "export … cannot be updated as it is in use", deploy the consuming stack first.

## Security checks (cdk-nag)

`npm test` runs the AwsSolutions pack for dev, staging, prod and two variants (NAT gateway + RDS
Proxy, Amplify enabled). The test fails on any finding that is not acknowledged with a reason in
[`lib/nag-suppressions.ts`](lib/nag-suppressions.ts). Current acknowledgments: single-AZ database,
default PostgreSQL port, no deletion protection outside production, optional MFA, no Cognito Plus
plan, no S3 / CloudFront access logs, no WAF / geo restriction on the media distribution, default
CloudFront certificate while no custom domain is configured, public API routes, NAT instance
(unencrypted root volume, no detailed monitoring, no termination protection), VPC flow logs off
outside production, the AWS managed `AWSLambdaBasicExecutionRole` of the CDK-owned bucket-notifications
handler (and of the auto-delete handler in dev), the image processor dead-letter queue having no dead-letter
queue of its own (SQS3), and the IAM wildcards listed below.

IAM wildcards (all acknowledged):

- `Resource: *` for the six EC2 network-interface actions every VPC Lambda needs (they do not
  support resource-level permissions). API and migration runner only; the image processor is outside the VPC.
- `…:log-group:/aws/lambda/<function>:*` – log streams of the function's own log group (API, migration runner,
  image processor).
- `<bucket>/uploads/*` and `<bucket>/media/*` – object prefixes (API and image processor).

The bucket-notifications handler's own policy is `s3:PutBucketNotification` on the media bucket only.

## Intentionally not built yet

Later phases add these; the code has extension points (comments in the stacks) but no stubs:

- Outbox worker Lambda and its EventBridge schedule.
- SES identity, configuration set and the API's permission to send e-mail.
- Cognito administration permissions for the API (inviting administrators).
- Tracking-token encryption key in Secrets Manager.
- Outbox / SES alarms and the AWS Budget alert.
- Custom domains for the Amplify apps, certificates and DNS records.
- Lambda cold-start optimisation (SnapStart / ReadyToRun) – to be evaluated.

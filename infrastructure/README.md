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
| `Notifications` | Tracking-code key secret (Secrets Manager); SES domain identity (prod only), SES configuration set with a CloudWatch event destination (staging, prod) |
| `Api` | API Lambda (ASP.NET Core, .NET 10, arm64) with alias `live`, HTTP API with JWT authorizer on `/api/v1/admin/*` and per-route throttling on tracking lookup and checkout; e-mail outbox worker Lambda with its EventBridge schedule (every minute) |
| `Observability` | SNS alarm topic, CloudWatch alarms (API 5xx / p95 latency, Lambda errors / throttles, image processor errors / dead-lettered uploads, outbox worker errors / throttles / duration / not running, dead e-mails, SES bounce / complaint rate (prod), RDS CPU / storage / connections, NAT status check); the account's AWS Budget (prod) |
| `Frontend` | Amplify Hosting apps (storefront, admin) built from the separate frontend repository. Only when `amplify.enabled` is `true` |

Dependencies point one way only (`Network ← Database ← Migrations / Api ← Observability`,
`Auth, Media, Notifications ← Api ← Frontend`, `Media ← Observability`), so there are no cyclic cross-stack references.
`Notifications` has no dependencies: the pipeline deploys it (and checks the tracking key) before anything else.

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
| `lambdaAssetPath` | `../backend/artifacts/lambda` | `Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync` (migration runner), `Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync` (image processor), `Zladovce.Lambda::Zladovce.Lambda.OutboxWorkerFunction::HandleAsync` (outbox worker) |

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
| `storefront.baseUrl` (`Storefront__BaseUrl`) | `https://dev.zladovce.com` (placeholder, no dev shop yet) | `https://staging.zladovce.com` | `https://zladovce.com` |
| `email.enabled` | **no** (worker runs, sends nothing) | yes | yes |
| `email.identity` | `none` | `import` `zladovce.com` (owned by prod) | `create` `zladovce.com`, MAIL FROM `mail.zladovce.com`, no hosted zone |
| `email.fromAddress` / `fromName` | – | `staging-porudzbine@zladovce.com` / `Zladovce (staging)` | `porudzbine@zladovce.com` / store name |
| `email.replyToAddress`, `email.logoUrl` | – | not set (store contact e-mail / no logo) | not set |
| `email.sandboxRecipients` | – | `[]` | `[]` |
| Tracking-key secret on stack deletion | deleted | deleted | retained |
| Outbox worker (`outboxWorker.*`) | 512 MB, 90 s, batch = backend default (50), reserved concurrency 1 | same | same |
| Route throttling (`api.routeThrottling`) | lookup 10 / 20, checkout 5 / 10 (rate / burst) | same | same |
| SES reputation alarms (`observability.sesReputationAlarms`) | no | no | yes (account-wide) |
| `budget` (account-wide) | – | – | 120 USD / month, alerts at 80 % and 100 % actual, 100 % forecast |

Notes:

- **`account`** is deliberately unset: stacks are environment-agnostic and deploy to whatever account
  the credentials belong to. The owner decided on **one AWS account for dev, staging and prod**; set the same
  account id in all three once it exists. Everything is named per environment (`zladovce-<env>-…`,
  `Zladovce-<Env>-…`, `zladovce/<env>/…`) and every IAM policy is scoped to the environment's own resources; the
  account-level singletons are listed under "Shared account" below.
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
  `api.reservedConcurrency` to `undefined` temporarily (that also drops the reservation of 1 of the migration
  runner and the outbox worker). The image processor has no reservation. **Shared account:** reservations of all
  environments add up – 3 × (API 10 + migrations 1 + worker 1) = 36 – and Lambda always keeps 100 unreserved, so
  the account quota must be at least 136 (the normal default of 1000 is plenty).
- **Timeouts.** The HTTP API gives up after 30 s (hard limit of the integration). `validateConfig` refuses an
  `api.timeoutSeconds` above that, and an `api.mediaReadyWaitSeconds` that is not below both `api.timeoutSeconds`
  (15) and 30 s – the attach endpoints must answer `409 IMAGE_NOT_READY` themselves, before Lambda or the gateway
  cut them off. (The backend additionally clamps the wait to 0–25 s.)

- **E-mail, SES and the tracking key:** see "Tracking codes and e-mail (Phase 6)" below.

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
Storefront__BaseUrl  Tracking__TokenEncryptionKeySecretArn  Email__Dispatcher__RunInProcess=false
```

The API encrypts each new tracking code into the outbox row (and refuses to start outside Development without
`Storefront__BaseUrl` and a key source), so it needs the key too; it never sends e-mail and has no SES permission.
`Email__Dispatcher__RunInProcess` is set to `false` explicitly – the in-process dispatcher is for local development only.

Outbox worker (`zladovce-<env>-outbox-worker`, VPC, private subnets, Lambda security group):

```
ASPNETCORE_ENVIRONMENT  DOTNET_ENVIRONMENT
Database__* as for the API (zladovce_app, IAM auth, same endpoint – the proxy when useRdsProxy is on)
Storefront__BaseUrl  Tracking__TokenEncryptionKeySecretArn  Email__Dispatcher__RunInProcess=false
Email__Enabled (true|false)  Email__Provider=Ses  Email__Ses__Region
Email__FromAddress  Email__Ses__ConfigurationSetName        (when the environment has them)
Email__FromName  Email__ReplyToAddress  Email__LogoUrl  Email__Dispatcher__BatchSize   (only when configured)
```

EventBridge rule `zladovce-<env>-outbox-worker`, `rate(1 minute)`, input ignored, no retries (EventBridge retry policy 0,
Lambda asynchronous retries 0, maximum event age 60 s: a failed or throttled run is simply replaced by the next one).
Timeout 90 s (the backend stops claiming e-mails when less than one send timeout of the run is left), reserved
concurrency 1 (parallel runs would be safe – rows are claimed with `FOR UPDATE SKIP LOCKED` – but one is enough).

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
| API (Phase 6 addition) | `secretsmanager:GetSecretValue` on the environment's tracking-key secret only |
| Outbox worker | `rds-db:connect` as `zladovce_app`; `secretsmanager:GetSecretValue` on the tracking-key secret; `ses:SendEmail` on the SES identity ARN and the environment's configuration set ARN (plus verified sandbox recipient identities, if configured), with condition `ses:FromAddress` = the environment's sender address. No SES permission at all while `email.enabled` is false. No S3 |

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

## Tracking codes and e-mail (Phase 6)

### Tracking-code key (Secrets Manager)

The backend encrypts plain tracking codes inside outbox rows with AES-256-GCM. The key is the secret
`zladovce/<env>/tracking-token-key` (stack `Notifications`); its value must be **base64 of 32 random bytes** (or
`{"key":"<base64>"}`).

Why it is not generated by CloudFormation: Secrets Manager's `GenerateSecretString` only produces passwords from a
character set; it cannot produce "base64 of exactly 32 bytes" (the last character before `=` is constrained), and a
custom resource that can would add a Lambda function, its role and more cdk-nag exceptions to maintain. Instead:

1. The stack creates the secret with a **placeholder** `{"status":"NOT-INITIALISED","filler":"<random>"}`. The backend
   rejects it (no `key` member), so it can never be used as a key by accident. The real key never appears in a template.
2. The key is written **once per environment**:
   ```
   aws secretsmanager put-secret-value --secret-id zladovce/<env>/tracking-token-key --secret-string "$(openssl rand -base64 32)"
   ```
   `deploy.yml` does exactly this automatically while the placeholder is still there (step "Verify (or initialise) the
   tracking-code key", after deploying `Notifications` and **before** migrations and the API), and fails the deployment
   when the value is anything else that is not a valid key (including the public development key). For a manual
   deployment, run the command above after the first `cdk deploy Zladovce-<Env>-Notifications`.
3. Never change `generateSecretString` in `NotificationsStack`: an update of that property makes CloudFormation generate
   a new value, which would overwrite the key. Never rotate the key while e-mails are queued (they could no longer be
   decrypted; the backend reads one key, no versions) – hence no automatic rotation (cdk-nag SMG4 acknowledged).
4. Removal policy: **retained** in prod, deleted with the stack in dev and staging. A deleted secret name stays reserved
   for its recovery window (7–30 days); recreating the stack within that window fails until the secret is restored
   (`aws secretsmanager restore-secret`) or force-deleted.

### SES in one shared account

SES identities, the sandbox status and sending quota, the account-level suppression list and the reputation metrics
belong to the **account and region**, not to an environment. Because dev, staging and prod share one account:

| Environment | Identity | Configuration set | Sends |
|---|---|---|---|
| prod | **creates** the domain identity `zladovce.com` (Easy DKIM 2048-bit, custom MAIL FROM `mail.zladovce.com`) | `zladovce-prod-email` | as `porudzbine@zladovce.com` |
| staging | **imports** `zladovce.com` by name (no resource) | `zladovce-staging-email` | as `staging-porudzbine@zladovce.com` |
| dev | none | none | nothing (`email.enabled=false`) |

- A domain identity can be created only once per account and region, so exactly one environment (prod) owns it
  (a unit test enforces this). Staging only builds the identity ARN for its IAM policy; deploy
  `Zladovce-Prod-Notifications` (it has no dependencies) before staging needs to send. The worker roles are
  pinned to their own sender with the `ses:FromAddress` condition, so staging cannot send as the prod address and
  vice versa. Alternative if staging should be fully independent: give it its own sub-domain identity
  (`mode: 'create'`, e.g. `staging.zladovce.com`) – that costs three more DKIM records.
- Configuration sets are named per environment, so each environment gets its own event metrics: the CloudWatch event
  destination publishes `Send`, `Delivery`, `Bounce`, `Complaint`, `Reject`, `DeliveryDelay` in `AWS/SES` with the
  dimension `ses:configuration-set` = `zladovce-<env>-email`. Bounced and complained addresses are put on the
  account-level suppression list (`SuppressedReasons: BOUNCE, COMPLAINT`).
- Reputation metrics (`Reputation.BounceRate`, `Reputation.ComplaintRate`) are account-wide; their alarms exist in prod
  only (`observability.sesReputationAlarms`).
- **Sandbox:** a new account can send only to verified addresses (200 e-mails / day). In the sandbox SES also checks the
  IAM permission on the **recipient** identity, so list verified test addresses in `email.sandboxRecipients` (verify them
  with `aws sesv2 create-email-identity --email-identity <address>` and the link SES sends – these identities are
  account-level too, create each address once). Leaving the sandbox (production access) is one request for the whole
  account; after it, `sandboxRecipients` should be empty.
- **Route 53 is not decided.** Without `email.identity.hostedZone`, the DKIM CNAME records are stack outputs
  (`DkimRecord1..3`), as are the MAIL FROM MX/SPF records and a suggested DMARC record – add them at the DNS provider.
  With a hosted zone, CDK creates the DKIM records (MAIL FROM and DMARC records are still added by hand). Without the
  MAIL FROM MX record SES falls back to its own MAIL FROM domain (`USE_DEFAULT_VALUE`) instead of refusing to send.
- **E-mail disabled** (`email.enabled=false`, dev): the worker is still deployed and scheduled with `Email__Enabled=false`.
  It then sends nothing – rows stay `Pending` and are sent once e-mail is enabled – but it still purges the expired
  rate-limit windows (`rate_limit_buckets`), which nothing else does. It gets no SES permission.

### HTTP API throttling

Stage default: `api.throttleRateLimit` / `throttleBurstLimit`. Stricter route settings on two explicit routes (same
integration, no authorizer; route settings need a real route key, so these routes exist besides `ANY /{proxy+}`):

| Route | Rate / burst | Backend limit behind it |
|---|---|---|
| `POST /api/v1/tracking/lookup` | 10 / 20 per second | 10 per 10 min per IP, 300 per minute overall |
| `POST /api/v1/orders` | 5 / 10 per second | 5 per 10 min per IP |

The gateway limits are coarse outer limits (above the backend's own global limits) that stop floods before they reach
Lambda and the database; normal clients see the backend's `429 RATE_LIMITED` with `Retry-After`. The gateway's own 429
has no problem body.

### Alarms (Phase 6)

| Alarm | Condition |
|---|---|
| `OutboxWorkerErrors` | ≥ `observability.lambdaErrorThreshold` (3) failed runs in 5 min (database / configuration problem; a failed e-mail does not fail a run) |
| `OutboxWorkerThrottles` | throttles in each of 3 consecutive 5-min periods (an occasional throttle after a long run is normal) |
| `OutboxWorkerDurationNearTimeout` | maximum duration ≥ timeout − 5 s (85 s) in 2 of 3 periods |
| `OutboxWorkerNotRunning` | fewer than 1 invocation in 15 min (missing data counts as breaching) |
| `OutboxDeadEmails` | ≥ 1 e-mail became Dead in 5 min – metric filter `"failed; now Dead"` on the worker log, i.e. the backend's `Email {OutboxId} ({Template}) attempt {Attempt} failed; now Dead.` warning, metric `Zladovce/<Env>` `OutboxDeadEmails` |
| `SesBounceRateHigh` (prod) | account `Reputation.BounceRate` ≥ 5 % (SES reviews at 5 %, may pause at 10 %) |
| `SesComplaintRateHigh` (prod) | account `Reputation.ComplaintRate` ≥ 0.1 % (SES reviews at 0.1 %, may pause at 0.5 %) |
| AWS Budget (prod, account-wide) | 80 % and 100 % of `budget.monthlyLimitUsd` actual, 100 % forecast → alarm topic (+ `budget.notificationEmails`) |

Known gaps (the backend publishes no outbox metric, and none is invented here): there is no "oldest pending e-mail"
or backlog-size alarm (docs/AWS_INFRASTRUCTURE.md W8 lists one), and rows marked Dead because a worker died during
their last attempt are only in the run summary line (`… {Dead} dead, {Expired} expired.`), not counted by the metric
filter. The admin e-mail list (status `Dead`/`Pending`) is the source of truth.

## Cost switches

| Switch | Default | Effect | Approx. cost |
|---|---|---|---|
| `network.natMode` | `instance` | `instance`: one `t4g.nano` NAT instance. `gateway`: managed NAT gateway | 7–8 vs 38–40 USD / month |
| `database.useRdsProxy` | `false` | Adds an RDS Proxy (end-to-end IAM authentication) and points the API at it; the migration runner keeps using the instance | +22 USD / month |
| `network.flowLogs` | prod only | VPC flow logs to CloudWatch | log volume |
| `media.autoDeleteObjects` | dev only | Empties the bucket when the stack is deleted (adds a CDK custom resource) | – |

When to change them is described in `docs/AWS_INFRASTRUCTURE.md` W4 (connection alarm firing, or
concurrency above ~30 → proxy) and W5 (NAT).

Phase 6 cost impact (small, per environment unless noted): Secrets Manager 0.40 USD / secret / month (one per
environment) plus API calls (the key is cached per Lambda instance); SES 0.10 USD per 1000 e-mails (no dedicated IP,
no VDM); EventBridge scheduled rules are free; the worker runs 43 200 times a month (512 MB, typically well under a
second when nothing is due – within or near the Lambda free tier); a few more alarms (0.10 USD each) and one metric
filter; AWS Budgets: the first two budgets of an account are free.

NAT instance notes: it runs Amazon Linux 2023 from the "latest" public SSM parameter, so a deployment
after a new AMI release **replaces the instance** (a short outbound outage; e-mails wait in the outbox).
It only forwards TCP 443 from the VPC. Its public IP is not static.

## First-time deploy checklist

Nothing below has been executed yet.

1. **Account and region.** One account for all three environments (owner's decision); set `account` in
   `lib/config.ts` for each environment.
2. **Bootstrap** each account/region once: `npx cdk bootstrap aws://<account>/eu-central-1`.
3. **Lambda quota.** Check the account's Lambda concurrency quota (see "Reserved concurrency" above; at least 136 for
   three environments).
4. **Publish the backend** packages (commands above).
5. **Deploy** – in this order, so that migrations run before the new API version is live:
   0. `npx cdk deploy Zladovce-<Env>-Notifications -c env=<env> -c requireArtifacts=true`, then set the tracking key
      once: `aws secretsmanager put-secret-value --secret-id zladovce/<env>/tracking-token-key --secret-string "$(openssl rand -base64 32)"`
      (`deploy.yml` does both automatically). Deploy `Zladovce-Prod-Notifications` first of all environments: it
      creates the SES identity staging uses.
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
   5. `curl <ApiUrl>/health`, and invoke the worker once:
      `aws lambda invoke --function-name zladovce-<env>-outbox-worker --payload '{}' --cli-binary-format raw-in-base64-out worker.json`
      (`deploy.yml` does both).
6. **First administrator.** Create the first user in the Cognito pool (console or CLI) and add it to
   the `Owner` group; self sign-up is disabled.
7. **Alarm e-mail.** Set `observability.alarmEmail` and confirm the subscription e-mail from SNS. The prod alarm topic
   also receives the AWS Budget alerts (optionally add `budget.notificationEmails`; check `budget.monthlyLimitUsd`).
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
10. **E-mail (SES), once per account** – nothing here can be done by CDK:
    1. After deploying `Zladovce-Prod-Notifications`, add the three DKIM CNAME records (outputs `DkimRecord1..3`), the
       MAIL FROM MX and SPF records (`MailFromMxRecord`, `MailFromSpfRecord`) and a DMARC record
       (`DmarcRecordSuggestion`, start with `p=none`) at the DNS provider of `zladovce.com`. Wait until the identity shows
       "Verified" (`aws sesv2 get-email-identity --email-identity zladovce.com`).
    2. Sender addresses: `porudzbine@zladovce.com` (prod) and `staging-porudzbine@zladovce.com` (staging) need no
       mailbox to send, but replies go to `email.replyToAddress` or the store contact e-mail from the store settings –
       make sure that one is a real, monitored mailbox. Change the addresses in `lib/config.ts` if wanted.
    3. While in the sandbox: verify test recipients (`aws sesv2 create-email-identity --email-identity <address>`, click
       the link) and list them in `email.sandboxRecipients` of the environment that sends to them.
    4. **Request production access** (SES console → Account dashboard → "Request production access"; transactional
       order e-mails, expected volume, how bounces/complaints are handled: account suppression list + alarms). It
       applies to the whole account. Then empty `email.sandboxRecipients`.
    5. `email.logoUrl` (optional): an https URL of the logo, e.g. on the media domain.
11. **Still to come (later phases):** Cognito e-mail through SES; custom domains and DNS records.
12. **CI/CD.** `.github/workflows/deploy.yml` needs a GitHub OIDC provider and deploy role in the
    account, and GitHub environments `staging` / `production` (with required reviewers) holding the
    variable `AWS_DEPLOY_ROLE_ARN`. Besides assuming the CDK bootstrap roles, the deploy role needs
    `lambda:InvokeFunction` on the migration runner and the outbox worker and `secretsmanager:GetSecretValue` +
    `PutSecretValue` on `zladovce/<env>/tracking-token-key-*` of the environments it deploys. The workflow is **untested**.

## Rollback

| What | How |
|---|---|
| API code | The HTTP API invokes the Lambda alias `live`. Point it back at the previous version: `aws lambda update-alias --function-name zladovce-<env>-api --name live --function-version <previous>`; then redeploy the previous commit so that CloudFormation and reality agree again. (CDK keeps old versions only until the next deployment replaces them – redeploying the previous commit is the reliable path.) |
| Infrastructure | Check out the previous commit and deploy it. Review `cdk diff` first |
| Database schema | Migrations are forward-only and backward-compatible, so a code rollback needs no schema rollback |
| Outbox worker | Same package as the migration runner; it has no alias – redeploy the previous commit. To stop sending at once: `aws events disable-rule --name zladovce-<env>-outbox-worker` (e-mails wait in the outbox) |
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
queue of its own (SQS3), no automatic rotation of the tracking-code key (SMG4, see "Tracking-code key"), and the IAM
wildcards listed below.

IAM wildcards (all acknowledged):

- `Resource: *` for the six EC2 network-interface actions every VPC Lambda needs (they do not
  support resource-level permissions). API, migration runner and outbox worker; the image processor is outside the VPC.
- `…:log-group:/aws/lambda/<function>:*` – log streams of the function's own log group (API, migration runner,
  image processor, outbox worker).

No new wildcard in Phase 6: `ses:SendEmail` names the identity and configuration set ARNs, and the tracking key is
granted by its full secret ARN.
- `<bucket>/uploads/*` and `<bucket>/media/*` – object prefixes (API and image processor).

The bucket-notifications handler's own policy is `s3:PutBucketNotification` on the media bucket only.

## Intentionally not built yet

Later phases add these; the code has extension points (comments in the stacks) but no stubs:

- Cognito administration permissions for the API (inviting administrators); Cognito e-mail through SES.
- An outbox backlog alarm (needs a metric the backend does not publish yet).
- Custom domains for the Amplify apps, certificates and DNS records (including Route 53 for the SES records).
- Lambda cold-start optimisation (SnapStart / ReadyToRun) – to be evaluated.

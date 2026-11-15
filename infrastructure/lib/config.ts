import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as rds from 'aws-cdk-lib/aws-rds';
import { Node } from 'constructs';

/**
 * Single typed configuration module for all Zladovce environments.
 *
 * Select an environment with `-c env=dev|staging|prod`. A few values can be
 * overridden from the command line (see `applyContextOverrides`).
 *
 * Nothing in this file is a secret. Secrets live in AWS Secrets Manager.
 */

export type EnvName = 'dev' | 'staging' | 'prod';
export const ENV_NAMES: readonly EnvName[] = ['dev', 'staging', 'prod'];

export type NatMode = 'instance' | 'gateway';

/* -------------------------------------------------------------------------- */
/* Constants shared by every environment                                       */
/* -------------------------------------------------------------------------- */

export const PROJECT_NAME = 'Zladovce';

/**
 * Lambda runtime for all .NET functions.
 * SINGLE PLACE TO CHANGE when the runtime is upgraded.
 */
export const DOTNET_LAMBDA_RUNTIME: lambda.Runtime = lambda.Runtime.DOTNET_10;

/**
 * PostgreSQL major version. Only the major is pinned; RDS picks the current
 * default minor and applies minor upgrades automatically.
 */
export const POSTGRES_ENGINE_VERSION: rds.PostgresEngineVersion = rds.PostgresEngineVersion.VER_17;

/** Handler for an ASP.NET Core app hosted with Amazon.Lambda.AspNetCoreServer.Hosting = assembly name. */
export const DEFAULT_API_HANDLER = 'Zladovce.Api';
/** assembly::type::method - must match backend/src/Zladovce.Lambda/MigrationRunner.cs. */
export const DEFAULT_MIGRATION_HANDLER = 'Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync';
/** assembly::type::method - must match backend/src/Zladovce.Lambda/ImageProcessorFunction.cs. */
export const DEFAULT_IMAGE_PROCESSOR_HANDLER = 'Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync';
/** assembly::type::method - must match backend/src/Zladovce.Lambda/OutboxWorkerFunction.cs. */
export const DEFAULT_OUTBOX_WORKER_HANDLER = 'Zladovce.Lambda::Zladovce.Lambda.OutboxWorkerFunction::HandleAsync';

export const DEFAULT_API_ASSET_PATH = '../backend/artifacts/api';
/** Publish directory of backend/src/Zladovce.Lambda: one package for every non-HTTP handler (migrations, images, outbox). */
export const DEFAULT_LAMBDA_ASSET_PATH = '../backend/artifacts/lambda';

/**
 * Maximum integration timeout of an API Gateway HTTP API (hard limit). The API answers `IMAGE_NOT_READY`
 * after `api.mediaReadyWaitSeconds`, so that wait must end before this and before the Lambda timeout.
 */
export const HTTP_API_INTEGRATION_TIMEOUT_SECONDS = 30;

/** S3 key prefixes. The API may only presign uploads under `uploads/`; CloudFront only serves `media/`. */
export const UPLOADS_PREFIX = 'uploads/';
export const MEDIA_PREFIX = 'media/';

/** HTTP API routes with their own (stricter) throttling. They are explicit routes because route settings need one. */
export const TRACKING_LOOKUP_ROUTE_PATH = '/api/v1/tracking/lookup';
export const CREATE_ORDER_ROUTE_PATH = '/api/v1/orders';

/**
 * Text of the log line the outbox worker writes when an e-mail becomes Dead after its last attempt
 * (backend/src/Zladovce.Infrastructure/Notifications/EmailDispatcher.cs, template
 * "Email {OutboxId} ({Template}) attempt {Attempt} failed; now {Status}." with Status = Dead).
 * The CloudWatch Logs metric filter matches this phrase. Keep it in sync with the backend.
 */
export const OUTBOX_DEAD_LOG_TEXT = 'failed; now Dead';

/**
 * Marker in the initial value of the tracking-key secret. Secrets Manager cannot generate "base64 of exactly
 * 32 bytes", so the secret is created as `{"status":"NOT-INITIALISED","filler":"<random>"}` and the real key is
 * written once with `put-secret-value` (README, deploy.yml). The backend rejects this value (no "key" member), so the
 * placeholder can never be used as a key by accident.
 */
export const TRACKING_KEY_PLACEHOLDER_MARKER = 'NOT-INITIALISED';

/* -------------------------------------------------------------------------- */
/* Types                                                                       */
/* -------------------------------------------------------------------------- */

export interface CustomDomainConfig {
  /** Fully qualified domain name, e.g. `api.zladovce.com`. */
  readonly domainName: string;
  /**
   * ARN of an existing ACM certificate. No certificate or DNS record is created
   * by this app (where DNS lives is still an open question).
   * API: certificate in the stack region. Media (CloudFront): certificate in us-east-1.
   */
  readonly certificateArn: string;
}

export interface DomainsConfig {
  /** Optional custom domain for the HTTP API. Without it the generated execute-api URL is used. */
  readonly api?: CustomDomainConfig;
  /** Optional custom domain for the media CloudFront distribution. */
  readonly media?: CustomDomainConfig;
}

export interface CorsConfig {
  /** Exact origins of the storefront (scheme + host [+ port], no trailing slash). */
  readonly storefrontOrigins: string[];
  /** Exact origins of the admin application. Also the only origins allowed to upload to S3. */
  readonly adminOrigins: string[];
}

export interface NetworkConfig {
  readonly cidr: string;
  /**
   * Explicit availability zones (exactly two). Explicit so that `cdk synth`
   * never needs an account lookup.
   */
  readonly availabilityZones: string[];
  /** COST SWITCH: `instance` = t4g.nano NAT instance (~7 USD/mo), `gateway` = managed NAT Gateway (~38 USD/mo). */
  readonly natMode: NatMode;
  readonly natInstanceType: string;
  /** VPC flow logs (rejected traffic only) to CloudWatch. */
  readonly flowLogs: boolean;
}

export interface DatabaseConfig {
  readonly instanceType: string;
  readonly allocatedStorageGb: number;
  readonly backupRetentionDays: number;
  readonly deletionProtection: boolean;
  /** What happens to the instance when the stack is deleted. */
  readonly removalPolicy: 'destroy' | 'snapshot';
  /** COST SWITCH: adds an RDS Proxy (~22 USD/mo) and points the API at it. */
  readonly useRdsProxy: boolean;
  readonly databaseName: string;
  readonly masterUsername: string;
  /** PostgreSQL role used by the API (data permissions only, IAM authentication). */
  readonly appUser: string;
  /** PostgreSQL role used by the migration runner (may change schema, IAM authentication). */
  readonly migrationUser: string;
  /**
   * Lets the migration runner read the RDS-managed master secret so that it can
   * create the two IAM-authenticated roles on a brand-new database. The database
   * is private, so nothing else can do that first step. Can be switched off once
   * the roles exist.
   */
  readonly migrationRunnerCanReadMasterSecret: boolean;
}

export interface AuthConfig {
  /** Keep the user pool when the stack is deleted, and enable deletion protection. */
  readonly retainOnDelete: boolean;
  readonly passwordMinLength: number;
  readonly accessTokenValidityMinutes: number;
  readonly idTokenValidityMinutes: number;
  readonly refreshTokenValidityHours: number;
}

export interface MediaConfig {
  readonly retainOnDelete: boolean;
  /** Only sensible for throw-away environments; adds a CDK custom resource that empties the bucket on delete. */
  readonly autoDeleteObjects: boolean;
  readonly uploadsExpirationDays: number;
  readonly noncurrentVersionExpirationDays: number;
  /** Image processor Lambda (S3 trigger on uploads/): memory. Magick.NET needs room for 1600 px renditions. */
  readonly processorMemoryMb: number;
  readonly processorTimeoutSeconds: number;
  /** Retries of a failed asynchronous invocation (0-2) before the event goes to the dead-letter queue. */
  readonly processorRetryAttempts: number;
  /** How long failed events stay in the dead-letter queue. */
  readonly deadLetterRetentionDays: number;
}

export interface ApiConfig {
  readonly memoryMb: number;
  readonly timeoutSeconds: number;
  /**
   * Reserved concurrency of the API function. `undefined` = no reservation.
   * NOTE: brand-new AWS accounts often have an account concurrency quota of 10,
   * in which case any reservation is rejected; request a quota increase or unset.
   */
  readonly reservedConcurrency?: number;
  readonly logRetentionDays: number;
  /** Npgsql pool size per Lambda instance (Database__MaxPoolSize). Concurrency x pool must stay well under ~80. */
  readonly dbMaxPoolSize: number;
  /** Default (stage-level) throttling of the HTTP API. */
  readonly throttleRateLimit: number;
  readonly throttleBurstLimit: number;
  /**
   * ASPNETCORE_ENVIRONMENT / DOTNET_ENVIRONMENT of the Lambda functions.
   * NEVER 'Development' in AWS: the backend's appsettings.Development.json points at a local database,
   * migrates on startup and enables the authentication bypass.
   */
  readonly aspnetEnvironment: 'Dev' | 'Staging' | 'Production';
  readonly apiAssetPath: string;
  readonly apiHandler: string;
  /** Publish directory of the Zladovce.Lambda package (migration runner and image processor). */
  readonly lambdaAssetPath: string;
  readonly migrationHandler: string;
  readonly imageProcessorHandler: string;
  readonly migrationTimeoutSeconds: number;
  /**
   * `Media__ReadyWaitSeconds`: how long the attach endpoints wait for the image processor before answering
   * 409 IMAGE_NOT_READY. Must be below `timeoutSeconds` and the HTTP API integration timeout (validated).
   */
  readonly mediaReadyWaitSeconds: number;
  /** Optional `Catalog__MaxOrderQuantity` (cap of `maxOrderQuantity` in the shop). Unset = backend default (20). */
  readonly maxOrderQuantity?: number;
  /** Stricter per-route throttling (HTTP API route settings) on top of the stage default. */
  readonly routeThrottling: RouteThrottlingConfig;
}

export interface ThrottleConfig {
  /** Steady-state requests per second. */
  readonly rateLimit: number;
  /** Burst (bucket size). */
  readonly burstLimit: number;
}

export interface RouteThrottlingConfig {
  /** `POST /api/v1/tracking/lookup`: coarse outer limit; the backend adds 10 / 10 min per IP and 300 / min overall. */
  readonly trackingLookup: ThrottleConfig;
  /** `POST /api/v1/orders` (checkout): the backend adds 5 / 10 min per IP. */
  readonly createOrder: ThrottleConfig;
}

export interface StorefrontConfig {
  /**
   * `Storefront__BaseUrl`: public https URL of the shop, no trailing slash (links in e-mails, tracking page).
   * Required by the backend outside Development, for the API and for the outbox worker.
   */
  readonly baseUrl: string;
}

export interface TrackingConfig {
  /** Keep the tracking-key secret when the stack is deleted (production: queued e-mails stay decryptable). */
  readonly retainKeyOnDelete: boolean;
}

/**
 * Which SES identity an environment sends from. SES identities are ACCOUNT-LEVEL singletons per region (a domain can
 * exist only once per account and region), and all three environments share one account. Exactly one environment
 * (prod) creates the domain identity; the others import it by name or have none.
 */
export type SesIdentityConfig =
  /** No identity, no configuration set, no SES permission (e-mail must be disabled). */
  | { readonly mode: 'none' }
  /** This environment creates and owns the domain identity (Easy DKIM, 2048-bit keys). */
  | {
      readonly mode: 'create';
      readonly domain: string;
      /**
       * Optional Route 53 public hosted zone of the domain: when set, CDK creates the DKIM CNAME records. Without it the
       * records are stack outputs to add at the DNS provider by hand. (MAIL FROM records are always added by hand.)
       */
      readonly hostedZone?: { readonly hostedZoneId: string; readonly zoneName: string };
      /** Optional custom MAIL FROM domain, a sub-domain of `domain` (e.g. `mail.zladovce.com`). Needs MX + SPF records. */
      readonly mailFromDomain?: string;
    }
  /** The identity is owned by another environment (prod) of the same account and only referenced by name. */
  | { readonly mode: 'import'; readonly domain: string };

export interface EmailConfig {
  /**
   * `Email__Enabled`. false: the outbox worker is still deployed and scheduled (it also purges the rate-limit windows)
   * but sends nothing; rows stay Pending and go out once e-mail is enabled.
   */
  readonly enabled: boolean;
  readonly identity: SesIdentityConfig;
  /** `Email__FromAddress`: an address of the identity's domain. Required while `enabled`. */
  readonly fromAddress?: string;
  /** `Email__FromName` (backend default: the store name). */
  readonly fromName?: string;
  /** `Email__ReplyToAddress` (backend default: the store contact e-mail). */
  readonly replyToAddress?: string;
  /** `Email__LogoUrl`: absolute https URL of the logo shown in e-mails. */
  readonly logoUrl?: string;
  /**
   * SES sandbox only: verified recipient addresses (identities verified by hand, see README). In the sandbox SES also
   * authorises the RECIPIENT identity, so the worker may send to these identity ARNs too. Empty after production access.
   */
  readonly sandboxRecipients: string[];
}

export interface OutboxWorkerConfig {
  readonly handler: string;
  readonly memoryMb: number;
  /** 60-120 s (backend recommendation; the row lease is 120 s). */
  readonly timeoutSeconds: number;
  /** `Email__Dispatcher__BatchSize`; undefined = backend default (50). */
  readonly batchSize?: number;
}

export interface BudgetConfig {
  /**
   * The AWS Budget covers the WHOLE ACCOUNT (dev, staging and prod share it). Configure it in exactly one environment
   * (prod) so that it exists once.
   */
  readonly monthlyLimitUsd: number;
  /** Extra e-mail subscribers. The budget always notifies the environment's alarm topic as well. */
  readonly notificationEmails: string[];
  /** Notify when ACTUAL spend passes these percentages of the limit. */
  readonly actualThresholdsPercent: number[];
  /** Notify when FORECASTED spend passes this percentage. */
  readonly forecastThresholdPercent?: number;
}

export interface ObservabilityConfig {
  /** Optional e-mail address subscribed to the alarm topic. */
  readonly alarmEmail?: string;
  readonly api5xxThreshold: number;
  readonly apiLatencyP95Ms: number;
  readonly lambdaErrorThreshold: number;
  readonly rdsCpuPercent: number;
  readonly rdsFreeStorageBytes: number;
  readonly rdsMaxConnections: number;
  /**
   * Alarms on the ACCOUNT-LEVEL SES reputation metrics (Reputation.BounceRate / ComplaintRate). They are the same for
   * every environment of the shared account, so only one environment (prod) creates them.
   */
  readonly sesReputationAlarms: boolean;
  /** Fraction, 0.05 = 5 % (SES reviews an account at 5 % and may pause sending at 10 %). */
  readonly sesBounceRateThreshold: number;
  /** Fraction, 0.001 = 0.1 % (SES reviews an account at 0.1 % and may pause sending at 0.5 %). */
  readonly sesComplaintRateThreshold: number;
}

export interface AmplifyConfig {
  /** Off by default: connecting a repository needs a Git provider token (or GitHub App connection) that does not exist yet. */
  readonly enabled: boolean;
  /**
   * Frontend repository URL (default: zladovce_web). The Angular workspace is at the repository root, so the
   * build specs in `amplify/` are single-application specs (no monorepo `appRoot`).
   */
  readonly repository?: string;
  readonly branch: string;
  /** Name of a Secrets Manager secret (plain string) that holds the Git provider access token. */
  readonly accessTokenSecretName?: string;
  /** true = `Content-Security-Policy-Report-Only`, false = enforcing `Content-Security-Policy`. */
  readonly cspReportOnly: boolean;
}

export interface EnvironmentConfig {
  readonly envName: EnvName;
  /** AWS account id. `undefined` = environment-agnostic synth (uses the credentials at deploy time). */
  readonly account?: string;
  readonly region: string;
  readonly isProduction: boolean;
  readonly domains: DomainsConfig;
  readonly cors: CorsConfig;
  readonly network: NetworkConfig;
  readonly database: DatabaseConfig;
  readonly auth: AuthConfig;
  readonly media: MediaConfig;
  readonly api: ApiConfig;
  readonly observability: ObservabilityConfig;
  readonly amplify: AmplifyConfig;
  readonly storefront: StorefrontConfig;
  readonly tracking: TrackingConfig;
  readonly email: EmailConfig;
  readonly outboxWorker: OutboxWorkerConfig;
  /** Account-level AWS Budget: set in prod only (one account for all environments). */
  readonly budget?: BudgetConfig;
}

/* -------------------------------------------------------------------------- */
/* Environments                                                                */
/* -------------------------------------------------------------------------- */

const DEFAULT_REGION = 'eu-central-1';
const GIB = 1024 * 1024 * 1024;

const baseNetwork: NetworkConfig = {
  cidr: '10.20.0.0/16',
  availabilityZones: [`${DEFAULT_REGION}a`, `${DEFAULT_REGION}b`],
  natMode: 'instance',
  natInstanceType: 't4g.nano',
  flowLogs: false,
};

const baseDatabase: DatabaseConfig = {
  instanceType: 't4g.micro',
  allocatedStorageGb: 20,
  backupRetentionDays: 1,
  deletionProtection: false,
  removalPolicy: 'destroy',
  useRdsProxy: false,
  databaseName: 'zladovce',
  masterUsername: 'zladovce_admin',
  appUser: 'zladovce_app',
  migrationUser: 'zladovce_migrator',
  migrationRunnerCanReadMasterSecret: true,
};

const baseAuth: AuthConfig = {
  retainOnDelete: false,
  passwordMinLength: 12,
  accessTokenValidityMinutes: 60,
  idTokenValidityMinutes: 60,
  refreshTokenValidityHours: 12,
};

const baseMedia: MediaConfig = {
  retainOnDelete: false,
  autoDeleteObjects: false,
  uploadsExpirationDays: 1,
  noncurrentVersionExpirationDays: 30,
  processorMemoryMb: 1024,
  processorTimeoutSeconds: 60,
  processorRetryAttempts: 2,
  deadLetterRetentionDays: 14,
};

const baseApi: Omit<ApiConfig, 'aspnetEnvironment' | 'logRetentionDays'> = {
  memoryMb: 1024,
  timeoutSeconds: 15,
  reservedConcurrency: 10,
  dbMaxPoolSize: 2,
  throttleRateLimit: 50,
  throttleBurstLimit: 100,
  apiAssetPath: DEFAULT_API_ASSET_PATH,
  apiHandler: DEFAULT_API_HANDLER,
  lambdaAssetPath: DEFAULT_LAMBDA_ASSET_PATH,
  migrationHandler: DEFAULT_MIGRATION_HANDLER,
  imageProcessorHandler: DEFAULT_IMAGE_PROCESSOR_HANDLER,
  migrationTimeoutSeconds: 300,
  mediaReadyWaitSeconds: 8,
  maxOrderQuantity: undefined,
  routeThrottling: {
    // Above the backend's own global lookup limit (300 / min = 5 / s), so normal users get the backend's
    // 429 RATE_LIMITED body; the gateway limit only stops floods before they reach Lambda and the database.
    trackingLookup: { rateLimit: 10, burstLimit: 20 },
    // A small shop takes a handful of orders per hour; the backend allows 5 per 10 minutes per IP.
    createOrder: { rateLimit: 5, burstLimit: 10 },
  },
};

const baseObservability: ObservabilityConfig = {
  alarmEmail: undefined,
  api5xxThreshold: 5,
  apiLatencyP95Ms: 3000,
  lambdaErrorThreshold: 3,
  rdsCpuPercent: 80,
  rdsFreeStorageBytes: 2 * GIB,
  rdsMaxConnections: 60,
  sesReputationAlarms: false,
  sesBounceRateThreshold: 0.05,
  sesComplaintRateThreshold: 0.001,
};

/** Sending domain. Its SES identity is created by prod only (see SesIdentityConfig). */
export const EMAIL_DOMAIN = 'zladovce.com';

const baseOutboxWorker: OutboxWorkerConfig = {
  handler: DEFAULT_OUTBOX_WORKER_HANDLER,
  memoryMb: 512,
  timeoutSeconds: 90,
  batchSize: undefined,
};

/** The frontend (storefront + admin Angular workspace) lives at the root of its own repository. */
export const FRONTEND_REPOSITORY = 'https://github.com/aleksandar-ilic/zladovce_web';

const baseAmplify: AmplifyConfig = {
  enabled: false,
  repository: FRONTEND_REPOSITORY,
  branch: 'main',
  accessTokenSecretName: undefined,
  cspReportOnly: true,
};

const environments: Record<EnvName, EnvironmentConfig> = {
  dev: {
    envName: 'dev',
    account: undefined, // TODO: set once the AWS account decision is made
    region: DEFAULT_REGION,
    isProduction: false,
    domains: {},
    cors: {
      // Local Angular dev servers. Add the generated Amplify URLs after the first Amplify deploy.
      storefrontOrigins: ['http://localhost:4200'],
      adminOrigins: ['http://localhost:4201'],
    },
    network: baseNetwork,
    database: baseDatabase,
    auth: baseAuth,
    media: { ...baseMedia, autoDeleteObjects: true },
    api: {
      ...baseApi,
      aspnetEnvironment: 'Dev',
      logRetentionDays: 30,
      throttleRateLimit: 20,
      throttleBurstLimit: 40,
    },
    observability: baseObservability,
    amplify: { ...baseAmplify, branch: 'develop' },
    // The backend requires an https URL outside Development. Dev has no public storefront yet and sends no e-mail,
    // so the value only ends up in outbox payloads. Replace with the dev Amplify URL once it exists.
    storefront: { baseUrl: 'https://dev.zladovce.com' },
    tracking: { retainKeyOnDelete: false },
    // No SES identity in dev: the worker runs with Email__Enabled=false (rows stay Pending, rate limits are purged).
    email: { enabled: false, identity: { mode: 'none' }, sandboxRecipients: [] },
    outboxWorker: baseOutboxWorker,
  },

  staging: {
    envName: 'staging',
    account: undefined, // TODO
    region: DEFAULT_REGION,
    isProduction: false,
    domains: {
      // api: { domainName: 'api-staging.zladovce.com', certificateArn: 'arn:aws:acm:eu-central-1:<account>:certificate/<id>' },
      // media: { domainName: 'media-staging.zladovce.com', certificateArn: 'arn:aws:acm:us-east-1:<account>:certificate/<id>' },
    },
    cors: {
      storefrontOrigins: ['https://staging.zladovce.com'],
      adminOrigins: ['https://admin-staging.zladovce.com'],
    },
    network: baseNetwork,
    database: { ...baseDatabase, backupRetentionDays: 1, removalPolicy: 'snapshot' },
    auth: baseAuth,
    media: baseMedia,
    api: { ...baseApi, aspnetEnvironment: 'Staging', logRetentionDays: 30 },
    observability: baseObservability,
    amplify: { ...baseAmplify, branch: 'main' },
    storefront: { baseUrl: 'https://staging.zladovce.com' },
    tracking: { retainKeyOnDelete: false },
    email: {
      enabled: true,
      // Owned by prod (deploy Zladovce-Prod-Notifications first); staging only references it by name.
      identity: { mode: 'import', domain: EMAIL_DOMAIN },
      fromAddress: `staging-porudzbine@${EMAIL_DOMAIN}`,
      fromName: 'Zladovce (staging)',
      replyToAddress: undefined,
      logoUrl: undefined,
      // While the account is in the SES sandbox: verified test recipients (see README).
      sandboxRecipients: [],
    },
    outboxWorker: baseOutboxWorker,
  },

  prod: {
    envName: 'prod',
    account: undefined, // TODO (a separate production account is recommended)
    region: DEFAULT_REGION,
    isProduction: true,
    domains: {
      // api: { domainName: 'api.zladovce.com', certificateArn: 'arn:aws:acm:eu-central-1:<account>:certificate/<id>' },
      // media: { domainName: 'media.zladovce.com', certificateArn: 'arn:aws:acm:us-east-1:<account>:certificate/<id>' },
    },
    cors: {
      storefrontOrigins: ['https://zladovce.com', 'https://www.zladovce.com'],
      adminOrigins: ['https://admin.zladovce.com'],
    },
    network: { ...baseNetwork, flowLogs: true },
    database: {
      ...baseDatabase,
      backupRetentionDays: 7,
      deletionProtection: true,
      removalPolicy: 'snapshot',
    },
    auth: { ...baseAuth, retainOnDelete: true },
    media: { ...baseMedia, retainOnDelete: true },
    api: { ...baseApi, aspnetEnvironment: 'Production', logRetentionDays: 90 },
    observability: { ...baseObservability, sesReputationAlarms: true },
    amplify: { ...baseAmplify, branch: 'production', cspReportOnly: true },
    storefront: { baseUrl: 'https://zladovce.com' },
    tracking: { retainKeyOnDelete: true },
    email: {
      enabled: true,
      identity: {
        mode: 'create',
        domain: EMAIL_DOMAIN,
        // Route 53 is not decided: the DKIM and MAIL FROM records are stack outputs, added at the DNS provider by hand.
        hostedZone: undefined,
        mailFromDomain: `mail.${EMAIL_DOMAIN}`,
      },
      fromAddress: `porudzbine@${EMAIL_DOMAIN}`,
      fromName: undefined, // backend default: the store name from the store settings
      replyToAddress: undefined, // backend default: the store contact e-mail
      logoUrl: undefined, // e.g. a logo on the media domain once it exists (https only)
      sandboxRecipients: [],
    },
    outboxWorker: baseOutboxWorker,
    // Covers the whole account (dev + staging + prod, roughly 3 x 30-40 USD, docs/AWS_INFRASTRUCTURE.md W10).
    budget: {
      monthlyLimitUsd: 120,
      notificationEmails: [],
      actualThresholdsPercent: [80, 100],
      forecastThresholdPercent: 100,
    },
  },
};

/* -------------------------------------------------------------------------- */
/* Access                                                                      */
/* -------------------------------------------------------------------------- */

export function isEnvName(value: unknown): value is EnvName {
  return typeof value === 'string' && (ENV_NAMES as readonly string[]).includes(value);
}

/** Returns a copy of the configuration of one environment. */
export function getEnvironmentConfig(envName: EnvName): EnvironmentConfig {
  return structuredClone(environments[envName]);
}

/** Deep-partial override helper used by tests and by `applyContextOverrides`. */
export type ConfigOverrides = {
  [K in keyof EnvironmentConfig]?: EnvironmentConfig[K] extends object | undefined
    ? Partial<NonNullable<EnvironmentConfig[K]>>
    : EnvironmentConfig[K];
};

export function withOverrides(config: EnvironmentConfig, overrides: ConfigOverrides): EnvironmentConfig {
  const result: Record<string, unknown> = { ...config };
  for (const [key, value] of Object.entries(overrides)) {
    if (value === undefined) continue;
    const current = (config as unknown as Record<string, unknown>)[key];
    result[key] =
      value !== null && typeof value === 'object' && !Array.isArray(value)
        ? { ...(current as object), ...(value as object) }
        : value;
  }
  return result as unknown as EnvironmentConfig;
}

function contextString(node: Node, key: string): string | undefined {
  const value = node.tryGetContext(key);
  if (value === undefined || value === null || value === '') return undefined;
  return String(value);
}

function contextBool(node: Node, key: string): boolean | undefined {
  const value = contextString(node, key);
  if (value === undefined) return undefined;
  if (value === 'true') return true;
  if (value === 'false') return false;
  throw new Error(`Context value '${key}' must be 'true' or 'false', got '${value}'.`);
}

/**
 * Command-line overrides (all optional):
 *   -c account=123456789012      -c region=eu-central-1
 *   -c natMode=instance|gateway  -c useRdsProxy=true|false
 *   -c apiAssetPath=<dir>        -c lambdaAssetPath=<dir>   (former name: migrationAssetPath)
 *   -c alarmEmail=<address>
 */
export function applyContextOverrides(config: EnvironmentConfig, node: Node): EnvironmentConfig {
  const natMode = contextString(node, 'natMode');
  if (natMode !== undefined && natMode !== 'instance' && natMode !== 'gateway') {
    throw new Error(`Context value 'natMode' must be 'instance' or 'gateway', got '${natMode}'.`);
  }
  const region = contextString(node, 'region');
  const useRdsProxy = contextBool(node, 'useRdsProxy');
  const apiAssetPath = contextString(node, 'apiAssetPath');
  // `migrationAssetPath` is the former name of `lambdaAssetPath` and is still accepted.
  const lambdaAssetPath = contextString(node, 'lambdaAssetPath') ?? contextString(node, 'migrationAssetPath');
  const alarmEmail = contextString(node, 'alarmEmail');

  return withOverrides(config, {
    account: contextString(node, 'account') ?? config.account,
    region: region ?? config.region,
    network: {
      ...(natMode ? { natMode } : {}),
      ...(region ? { availabilityZones: [`${region}a`, `${region}b`] } : {}),
    },
    database: useRdsProxy === undefined ? {} : { useRdsProxy },
    api: {
      ...(apiAssetPath ? { apiAssetPath } : {}),
      ...(lambdaAssetPath ? { lambdaAssetPath } : {}),
    },
    observability: alarmEmail ? { alarmEmail } : {},
  });
}

/** All browser origins that may call the API. */
export function allCorsOrigins(config: EnvironmentConfig): string[] {
  return [...new Set([...config.cors.storefrontOrigins, ...config.cors.adminOrigins])];
}

/** `Dev`, `Staging`, `Prod` – used in stack names. */
export function envTitle(envName: EnvName): string {
  return envName.charAt(0).toUpperCase() + envName.slice(1);
}

/** Lower-case resource name prefix, e.g. `zladovce-dev`. */
export function resourcePrefix(config: EnvironmentConfig): string {
  return `zladovce-${config.envName}`;
}

export function validateConfig(config: EnvironmentConfig): void {
  const errors: string[] = [];
  if (config.network.availabilityZones.length !== 2) {
    errors.push('network.availabilityZones must contain exactly two zones.');
  }
  for (const az of config.network.availabilityZones) {
    if (!az.startsWith(config.region)) errors.push(`Availability zone '${az}' is not in region '${config.region}'.`);
  }
  for (const origin of allCorsOrigins(config)) {
    if (!/^https?:\/\/[^/*]+$/.test(origin)) {
      errors.push(`CORS origin '${origin}' must be an exact origin (scheme://host[:port], no path, no wildcard).`);
    }
  }
  if (config.cors.adminOrigins.length === 0) errors.push('cors.adminOrigins must not be empty.');
  if (config.account !== undefined && !/^\d{12}$/.test(config.account)) {
    errors.push(`account '${config.account}' is not a 12-digit AWS account id.`);
  }
  if (config.amplify.enabled) {
    if (!config.amplify.repository) errors.push('amplify.repository is required when amplify.enabled is true.');
    if (!config.amplify.accessTokenSecretName) {
      errors.push('amplify.accessTokenSecretName is required when amplify.enabled is true.');
    }
  }
  if (config.api.reservedConcurrency !== undefined && config.api.reservedConcurrency < 1) {
    errors.push('api.reservedConcurrency must be at least 1 (or undefined).');
  }
  if (config.api.timeoutSeconds > HTTP_API_INTEGRATION_TIMEOUT_SECONDS) {
    errors.push(
      `api.timeoutSeconds (${config.api.timeoutSeconds}) exceeds the HTTP API integration timeout ` +
        `(${HTTP_API_INTEGRATION_TIMEOUT_SECONDS} s); the gateway would give up first.`,
    );
  }
  const wait = config.api.mediaReadyWaitSeconds;
  if (!Number.isInteger(wait) || wait < 0) {
    errors.push('api.mediaReadyWaitSeconds must be a whole number of seconds, 0 or more.');
  }
  if (wait >= config.api.timeoutSeconds) {
    errors.push(
      `api.mediaReadyWaitSeconds (${wait}) must be below the API Lambda timeout api.timeoutSeconds (${config.api.timeoutSeconds}).`,
    );
  }
  if (wait >= HTTP_API_INTEGRATION_TIMEOUT_SECONDS) {
    errors.push(
      `api.mediaReadyWaitSeconds (${wait}) must be below the HTTP API integration timeout (${HTTP_API_INTEGRATION_TIMEOUT_SECONDS} s).`,
    );
  }
  const maxOrderQuantity = config.api.maxOrderQuantity;
  if (maxOrderQuantity !== undefined && (!Number.isInteger(maxOrderQuantity) || maxOrderQuantity < 1)) {
    errors.push('api.maxOrderQuantity must be a whole number, 1 or more (or undefined).');
  }
  const retries = config.media.processorRetryAttempts;
  if (!Number.isInteger(retries) || retries < 0 || retries > 2) {
    errors.push('media.processorRetryAttempts must be 0, 1 or 2 (limit of Lambda asynchronous invocations).');
  }
  if (config.media.processorTimeoutSeconds < 1 || config.media.processorTimeoutSeconds > 900) {
    errors.push('media.processorTimeoutSeconds must be between 1 and 900.');
  }
  errors.push(...validateNotifications(config));

  if (errors.length > 0) {
    throw new Error(`Invalid configuration for '${config.envName}':\n - ${errors.join('\n - ')}`);
  }
}

const HTTPS_URL = /^https:\/\/[^/?#\s]+(\/[^?#\s]*)?$/;
const EMAIL_ADDRESS = /^[^@\s]+@([^@\s]+\.[^@\s]+)$/;
const DOMAIN_NAME = /^(?!-)[a-z0-9-]+(\.[a-z0-9-]+)+$/;

/** The domain of the SES identity, or undefined for `mode: 'none'`. */
export function emailIdentityDomain(config: EnvironmentConfig): string | undefined {
  return config.email.identity.mode === 'none' ? undefined : config.email.identity.domain;
}

/** Name of the environment's SES configuration set (exists whenever the environment has an identity). */
export function configurationSetName(config: EnvironmentConfig): string | undefined {
  return emailIdentityDomain(config) === undefined ? undefined : `${resourcePrefix(config)}-email`;
}

function validateNotifications(config: EnvironmentConfig): string[] {
  const errors: string[] = [];
  const { storefront, email, outboxWorker, budget, api } = config;

  if (!HTTPS_URL.test(storefront.baseUrl) || storefront.baseUrl.endsWith('/')) {
    errors.push(`storefront.baseUrl '${storefront.baseUrl}' must be an absolute https URL without trailing slash, query or fragment.`);
  }

  const identity = email.identity;
  if (identity.mode !== 'none') {
    if (!DOMAIN_NAME.test(identity.domain)) errors.push(`email.identity.domain '${identity.domain}' is not a domain name.`);
    if (identity.mode === 'create') {
      if (identity.mailFromDomain !== undefined && !identity.mailFromDomain.endsWith(`.${identity.domain}`)) {
        errors.push(`email.identity.mailFromDomain '${identity.mailFromDomain}' must be a sub-domain of '${identity.domain}'.`);
      }
      const zone = identity.hostedZone;
      if (zone && identity.domain !== zone.zoneName && !identity.domain.endsWith(`.${zone.zoneName}`)) {
        errors.push(`email.identity.hostedZone '${zone.zoneName}' does not contain '${identity.domain}'.`);
      }
    }
  }
  if (email.enabled) {
    if (identity.mode === 'none') errors.push("email.enabled requires an SES identity (email.identity.mode 'create' or 'import').");
    if (!email.fromAddress) errors.push('email.fromAddress is required while email.enabled is true.');
  }
  if (email.fromAddress !== undefined) {
    const match = EMAIL_ADDRESS.exec(email.fromAddress);
    if (!match) {
      errors.push(`email.fromAddress '${email.fromAddress}' is not an e-mail address.`);
    } else if (identity.mode !== 'none' && match[1].toLowerCase() !== identity.domain) {
      errors.push(`email.fromAddress '${email.fromAddress}' must be an address of the identity domain '${identity.domain}'.`);
    }
  }
  for (const address of [email.replyToAddress, ...email.sandboxRecipients]) {
    if (address !== undefined && !EMAIL_ADDRESS.test(address)) errors.push(`'${address}' is not an e-mail address.`);
  }
  if (email.sandboxRecipients.length > 0 && identity.mode === 'none') {
    errors.push('email.sandboxRecipients needs an SES identity.');
  }
  if (email.logoUrl !== undefined && !HTTPS_URL.test(email.logoUrl)) {
    errors.push(`email.logoUrl '${email.logoUrl}' must be an absolute https URL.`);
  }

  if (!Number.isInteger(outboxWorker.timeoutSeconds) || outboxWorker.timeoutSeconds < 60 || outboxWorker.timeoutSeconds > 120) {
    errors.push('outboxWorker.timeoutSeconds must be a whole number between 60 and 120 (the e-mail lease is 120 s).');
  }
  if (outboxWorker.batchSize !== undefined && (!Number.isInteger(outboxWorker.batchSize) || outboxWorker.batchSize < 1)) {
    errors.push('outboxWorker.batchSize must be a whole number, 1 or more (or undefined).');
  }

  for (const [name, throttle] of Object.entries(api.routeThrottling)) {
    if (!(throttle.rateLimit > 0) || !Number.isInteger(throttle.burstLimit) || throttle.burstLimit < 1) {
      errors.push(`api.routeThrottling.${name} needs a positive rateLimit and a whole burstLimit of 1 or more.`);
    }
  }

  if (budget) {
    if (!(budget.monthlyLimitUsd > 0)) errors.push('budget.monthlyLimitUsd must be positive.');
    if (budget.notificationEmails.some((address) => !EMAIL_ADDRESS.test(address))) {
      errors.push('budget.notificationEmails must contain e-mail addresses only.');
    }
    const thresholds = [...budget.actualThresholdsPercent, ...(budget.forecastThresholdPercent ? [budget.forecastThresholdPercent] : [])];
    if (thresholds.length === 0 || thresholds.some((value) => !(value > 0) || value > 1000)) {
      errors.push('budget needs at least one threshold, each between 0 and 1000 percent.');
    }
  }
  return errors;
}

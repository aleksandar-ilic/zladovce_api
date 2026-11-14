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

export const DEFAULT_API_ASSET_PATH = '../backend/artifacts/api';
/** Publish directory of backend/src/Zladovce.Lambda: one package for every non-HTTP handler (migrations, images). */
export const DEFAULT_LAMBDA_ASSET_PATH = '../backend/artifacts/lambda';

/**
 * Maximum integration timeout of an API Gateway HTTP API (hard limit). The API answers `IMAGE_NOT_READY`
 * after `api.mediaReadyWaitSeconds`, so that wait must end before this and before the Lambda timeout.
 */
export const HTTP_API_INTEGRATION_TIMEOUT_SECONDS = 30;

/** S3 key prefixes. The API may only presign uploads under `uploads/`; CloudFront only serves `media/`. */
export const UPLOADS_PREFIX = 'uploads/';
export const MEDIA_PREFIX = 'media/';

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
};

const baseObservability: ObservabilityConfig = {
  alarmEmail: undefined,
  api5xxThreshold: 5,
  apiLatencyP95Ms: 3000,
  lambdaErrorThreshold: 3,
  rdsCpuPercent: 80,
  rdsFreeStorageBytes: 2 * GIB,
  rdsMaxConnections: 60,
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
    observability: baseObservability,
    amplify: { ...baseAmplify, branch: 'production', cspReportOnly: true },
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

  if (errors.length > 0) {
    throw new Error(`Invalid configuration for '${config.envName}':\n - ${errors.join('\n - ')}`);
  }
}

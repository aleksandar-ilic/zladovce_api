import { App, Tags } from 'aws-cdk-lib';
import { EnvironmentConfig, envTitle, PROJECT_NAME, validateConfig } from './config';
import { applyNagSuppressions } from './nag-suppressions';
import { ApiStack } from './stacks/api-stack';
import { AuthStack } from './stacks/auth-stack';
import { DatabaseStack } from './stacks/database-stack';
import { FrontendStack } from './stacks/frontend-stack';
import { MediaStack } from './stacks/media-stack';
import { MigrationStack } from './stacks/migration-stack';
import { NetworkStack } from './stacks/network-stack';
import { NotificationsStack } from './stacks/notifications-stack';
import { ObservabilityStack } from './stacks/observability-stack';

export interface ZladovceEnvironmentOptions {
  /** Fail when a backend Lambda artifact is missing (`-c requireArtifacts=true`). */
  readonly requireArtifacts?: boolean;
}

export interface ZladovceEnvironment {
  readonly config: EnvironmentConfig;
  readonly network: NetworkStack;
  readonly database: DatabaseStack;
  readonly auth: AuthStack;
  readonly media: MediaStack;
  readonly migrations: MigrationStack;
  readonly notifications: NotificationsStack;
  readonly api: ApiStack;
  readonly observability: ObservabilityStack;
  readonly frontend?: FrontendStack;
}

/**
 * Creates all stacks of one environment. Dependencies only point "downwards":
 *
 *   Network  <- Database <- Migrations
 *   Network  <- Database <- Api <- Observability
 *   Auth     <------------- Api <- Frontend (optional)
 *   Media    <------------- Api
 *   Notifications <-------- Api (tracking-key secret, SES identity / configuration set)
 *   Media    <------------------- Observability (image processor function and dead-letter queue)
 *
 * so there are no cyclic cross-stack references. The image processor and its S3 trigger are inside
 * the Media stack for exactly that reason (see MediaStack). Notifications has no dependencies, so the
 * pipeline can deploy it (and check the tracking key) before the API stack.
 */
export function createZladovceEnvironment(
  app: App,
  config: EnvironmentConfig,
  options: ZladovceEnvironmentOptions = {},
): ZladovceEnvironment {
  validateConfig(config);

  const stackName = (part: string): string => `${PROJECT_NAME}-${envTitle(config.envName)}-${part}`;
  const common = {
    // account undefined => environment-agnostic: synthesises without credentials.
    env: { account: config.account, region: config.region },
    terminationProtection: config.isProduction,
  };

  const network = new NetworkStack(app, stackName('Network'), {
    ...common,
    description: `Zladovce ${config.envName}: VPC, NAT, security groups`,
    config,
  });

  const database = new DatabaseStack(app, stackName('Database'), {
    ...common,
    description: `Zladovce ${config.envName}: RDS PostgreSQL`,
    config,
    vpc: network.vpc,
    databaseSecurityGroup: network.databaseSecurityGroup,
    proxySecurityGroup: network.proxySecurityGroup,
  });

  const auth = new AuthStack(app, stackName('Auth'), {
    ...common,
    description: `Zladovce ${config.envName}: Cognito user pool for administrators`,
    config,
  });

  const media = new MediaStack(app, stackName('Media'), {
    ...common,
    description: `Zladovce ${config.envName}: media bucket, CloudFront and image processor`,
    config,
    requireArtifacts: options.requireArtifacts ?? false,
  });

  const migrations = new MigrationStack(app, stackName('Migrations'), {
    ...common,
    description: `Zladovce ${config.envName}: database migration runner Lambda`,
    config,
    requireArtifacts: options.requireArtifacts ?? false,
    vpc: network.vpc,
    lambdaSecurityGroup: network.lambdaSecurityGroup,
    database,
  });

  const notifications = new NotificationsStack(app, stackName('Notifications'), {
    ...common,
    description: `Zladovce ${config.envName}: tracking-code key secret, SES identity and configuration set`,
    config,
  });

  const api = new ApiStack(app, stackName('Api'), {
    ...common,
    description: `Zladovce ${config.envName}: API Lambda and HTTP API`,
    config,
    requireArtifacts: options.requireArtifacts ?? false,
    vpc: network.vpc,
    lambdaSecurityGroup: network.lambdaSecurityGroup,
    database,
    userPoolId: auth.userPool.userPoolId,
    userPoolClientId: auth.adminClient.userPoolClientId,
    jwtIssuerUrl: auth.issuerUrl,
    mediaBucket: media.bucket,
    mediaBaseUrl: media.mediaBaseUrl,
    notifications,
  });

  const observability = new ObservabilityStack(app, stackName('Observability'), {
    ...common,
    description: `Zladovce ${config.envName}: alarm topic and CloudWatch alarms`,
    config,
    httpApi: api.httpApi,
    apiFunction: api.apiFunction.function,
    migrationFunction: migrations.migrationFunction.function,
    imageProcessorFunction: media.imageProcessor.function,
    imageProcessorDeadLetterQueue: media.imageProcessorDeadLetterQueue,
    outboxWorker: api.outboxWorker,
    databaseInstance: database.instance,
    natInstanceIds: network.natInstanceIds,
  });

  const frontend = config.amplify.enabled
    ? new FrontendStack(app, stackName('Frontend'), {
        ...common,
        description: `Zladovce ${config.envName}: Amplify Hosting (storefront and admin)`,
        config,
        apiUrl: api.apiUrl,
        mediaBaseUrl: media.mediaBaseUrl,
        mediaBucketName: media.bucket.bucketName,
        userPoolId: auth.userPool.userPoolId,
        userPoolClientId: auth.adminClient.userPoolClientId,
      })
    : undefined;

  const stacks = [
    network,
    database,
    auth,
    media,
    migrations,
    notifications,
    api,
    observability,
    ...(frontend ? [frontend] : []),
  ];
  for (const stack of stacks) {
    // AWS::RDS::DBProxyTargetGroup does not accept tags (CloudFormation schema validation fails).
    const tagOptions = { excludeResourceTypes: ['AWS::RDS::DBProxyTargetGroup'] };
    Tags.of(stack).add('Project', PROJECT_NAME, tagOptions);
    Tags.of(stack).add('Environment', config.envName, tagOptions);
  }

  const environment: ZladovceEnvironment = {
    config,
    network,
    database,
    auth,
    media,
    migrations,
    notifications,
    api,
    observability,
    frontend,
  };
  applyNagSuppressions(environment);
  return environment;
}

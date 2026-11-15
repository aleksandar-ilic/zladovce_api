import { ArnFormat, CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import { AccessLogFormat } from 'aws-cdk-lib/aws-apigateway';
import * as apigwv2 from 'aws-cdk-lib/aws-apigatewayv2';
import { HttpJwtAuthorizer } from 'aws-cdk-lib/aws-apigatewayv2-authorizers';
import { HttpLambdaIntegration } from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as events from 'aws-cdk-lib/aws-events';
import * as eventTargets from 'aws-cdk-lib/aws-events-targets';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';
import {
  allCorsOrigins,
  CREATE_ORDER_ROUTE_PATH,
  EnvironmentConfig,
  MEDIA_PREFIX,
  resourcePrefix,
  ThrottleConfig,
  TRACKING_LOOKUP_ROUTE_PATH,
  UPLOADS_PREFIX,
} from '../config';
import { DotnetFunction } from '../constructs/dotnet-function';
import { DatabaseStack } from './database-stack';
import { NotificationsStack } from './notifications-stack';

export interface ApiStackProps extends StackProps {
  readonly config: EnvironmentConfig;
  /** `-c requireArtifacts=true`: fail when a backend artifact is missing. */
  readonly requireArtifacts: boolean;
  readonly vpc: ec2.IVpc;
  readonly lambdaSecurityGroup: ec2.ISecurityGroup;
  readonly database: DatabaseStack;
  readonly userPoolId: string;
  readonly userPoolClientId: string;
  readonly jwtIssuerUrl: string;
  readonly mediaBucket: s3.IBucket;
  readonly mediaBaseUrl: string;
  /** Tracking-key secret and SES identity / configuration set. */
  readonly notifications: NotificationsStack;
}

export const ADMIN_ROUTE = '/api/v1/admin/{proxy+}';
export const PUBLIC_ROUTE = '/{proxy+}';
/** Alias the HTTP API invokes; move it back to a previous version to roll back code. */
export const LIVE_ALIAS = 'live';

/** Route keys with their own throttling (must be real routes: route settings for unknown keys fail to deploy). */
export const TRACKING_LOOKUP_ROUTE_KEY = `POST ${TRACKING_LOOKUP_ROUTE_PATH}`;
export const CREATE_ORDER_ROUTE_KEY = `POST ${CREATE_ORDER_ROUTE_PATH}`;

/**
 * API Lambda (ASP.NET Core), the HTTP API in front of it, and the outbox worker Lambda (EventBridge schedule) that
 * sends the e-mails the API queues. The worker is here because it runs the same application version against the same
 * schema: the pipeline deploys both together, after the migrations.
 * The migration runner lives in MigrationStack so that the pipeline can deploy and
 * run it BEFORE this stack publishes a new API version.
 *
 * Extension point (later phases): Cognito admin permissions are added to `apiFunction.role`.
 */
export class ApiStack extends Stack {
  public readonly apiFunction: DotnetFunction;
  public readonly outboxWorker: DotnetFunction;
  public readonly outboxWorkerSchedule: events.Rule;
  public readonly apiAlias: lambda.Alias;
  public readonly httpApi: apigwv2.HttpApi;
  public readonly stage: apigwv2.HttpStage;
  /** Routes that intentionally have no authorizer (public API and admin CORS preflight). */
  public readonly unauthenticatedRoutes: apigwv2.HttpRoute[];
  /** Base URL without trailing slash. */
  public readonly apiUrl: string;

  constructor(scope: Construct, id: string, props: ApiStackProps) {
    super(scope, id, props);
    const { config, database, notifications } = props;
    const prefix = resourcePrefix(config);
    const db = config.database;

    // Same database settings for the API and the outbox worker: the app role, IAM authentication.
    const databaseEnvironment: Record<string, string> = {
      Database__Host: database.appEndpointAddress,
      Database__Port: database.port,
      Database__Name: db.databaseName,
      Database__User: db.appUser,
      Database__UseIamAuth: 'true',
      Database__Region: this.region,
      Database__MaxPoolSize: String(config.api.dbMaxPoolSize),
    };
    // Every order writes an encrypted tracking code and links into the outbox, so the API needs these as well as the
    // worker (the backend refuses to start without them outside Development). The in-process dispatcher is for local
    // development only and must never run inside Lambda: the worker below sends.
    const trackingEnvironment: Record<string, string> = {
      Storefront__BaseUrl: config.storefront.baseUrl,
      Tracking__TokenEncryptionKeySecretArn: notifications.trackingKeySecret.secretArn,
      Email__Dispatcher__RunInProcess: 'false',
    };
    const readTrackingKey = (): iam.PolicyStatement =>
      new iam.PolicyStatement({
        sid: 'ReadTrackingKey',
        actions: ['secretsmanager:GetSecretValue'],
        resources: [notifications.trackingKeySecret.secretArn],
      });

    /* ------------------------------- API function ------------------------------- */

    const corsEnvironment: Record<string, string> = {};
    allCorsOrigins(config).forEach((origin, index) => {
      corsEnvironment[`Cors__AllowedOrigins__${index}`] = origin;
    });

    this.apiFunction = new DotnetFunction(this, 'Api', {
      functionName: `${prefix}-api`,
      description: `Zladovce ${config.envName} API (ASP.NET Core)`,
      handler: config.api.apiHandler,
      assetPath: config.api.apiAssetPath,
      requireArtifacts: props.requireArtifacts,
      memoryMb: config.api.memoryMb,
      timeout: Duration.seconds(config.api.timeoutSeconds),
      reservedConcurrency: config.api.reservedConcurrency,
      logRetentionDays: config.api.logRetentionDays,
      retainLogs: config.isProduction,
      vpc: props.vpc,
      securityGroup: props.lambdaSecurityGroup,
      environment: {
        ASPNETCORE_ENVIRONMENT: config.api.aspnetEnvironment,
        ...databaseEnvironment,
        ...trackingEnvironment,
        // Key names match the backend options (Auth:Cognito:*).
        Auth__Cognito__Region: this.region,
        Auth__Cognito__UserPoolId: props.userPoolId,
        Auth__Cognito__ClientId: props.userPoolClientId,
        Media__BucketName: props.mediaBucket.bucketName,
        Media__BaseUrl: props.mediaBaseUrl,
        Media__Region: this.region,
        // Uploads are processed by the image processor Lambda (MediaStack); the attach endpoints wait
        // up to Media__ReadyWaitSeconds for its result (validated to end before both timeouts).
        Media__ProcessInline: 'false',
        Media__ReadyWaitSeconds: String(config.api.mediaReadyWaitSeconds),
        ...(config.api.maxOrderQuantity !== undefined
          ? { Catalog__MaxOrderQuantity: String(config.api.maxOrderQuantity) }
          : {}),
        ...corsEnvironment,
      },
      policyStatements: [
        // Database: IAM authentication as the application user only.
        database.appConnectStatement(db.appUser),
        // Tracking codes are encrypted into outbox rows (and decrypted for an admin retry) with this key.
        readTrackingKey(),
        // Media: the pre-signed POST is signed with this role, so the browser upload is authorised by
        // s3:PutObject on uploads/*. Attaching reads the manifest under media/; deleting an image (or
        // archiving/replacing a category image) deletes its renditions under media/.
        new iam.PolicyStatement({
          sid: 'PresignUploads',
          actions: ['s3:PutObject'],
          resources: [props.mediaBucket.arnForObjects(`${UPLOADS_PREFIX}*`)],
        }),
        new iam.PolicyStatement({
          sid: 'ReadAndDeleteMedia',
          actions: ['s3:GetObject', 's3:DeleteObject'],
          resources: [props.mediaBucket.arnForObjects(`${MEDIA_PREFIX}*`)],
        }),
        // "Manifest not written yet" must be a 404 (keep waiting / IMAGE_NOT_READY), not a 403 (error).
        // S3 answers 404 for a missing object only to principals with s3:ListBucket on the bucket, and it
        // evaluates that permission without an s3:prefix value, so a prefix condition would turn every
        // 404 back into 403. Hence ListBucket on this one bucket, without a condition.
        new iam.PolicyStatement({
          sid: 'ListMediaBucket',
          actions: ['s3:ListBucket'],
          resources: [props.mediaBucket.bucketArn],
        }),
      ],
    });

    this.apiAlias = new lambda.Alias(this, 'ApiLiveAlias', {
      aliasName: LIVE_ALIAS,
      version: this.apiFunction.function.currentVersion,
    });

    /* ------------------------------- Outbox worker ------------------------------- */

    this.outboxWorker = this.createOutboxWorker(props, databaseEnvironment, trackingEnvironment, readTrackingKey());

    // Every minute; the event content is ignored. Deployed even when e-mail is disabled: the worker also purges the
    // expired rate-limit windows, and Pending rows go out as soon as e-mail is switched on.
    this.outboxWorkerSchedule = new events.Rule(this, 'OutboxWorkerSchedule', {
      ruleName: `${prefix}-outbox-worker`,
      description: `Zladovce ${config.envName}: run the e-mail outbox worker every minute`,
      schedule: events.Schedule.rate(Duration.minutes(1)),
      targets: [
        // No retries: the next scheduled run picks up whatever this one left.
        new eventTargets.LambdaFunction(this.outboxWorker.function, { retryAttempts: 0, maxEventAge: Duration.minutes(1) }),
      ],
    });

    /* --------------------------------- HTTP API --------------------------------- */

    // CORS is handled by the ASP.NET Core application (exact origin allow-list from
    // Cors__AllowedOrigins__N). No gateway-level CORS is configured, so there is one source of truth.
    this.httpApi = new apigwv2.HttpApi(this, 'HttpApi', {
      apiName: `${prefix}-api`,
      description: `Zladovce ${config.envName} HTTP API`,
      createDefaultStage: false,
    });

    const integration = new HttpLambdaIntegration('ApiLambdaIntegration', this.apiAlias, {
      payloadFormatVersion: apigwv2.PayloadFormatVersion.VERSION_2_0,
    });

    const adminAuthorizer = new HttpJwtAuthorizer('AdminJwtAuthorizer', props.jwtIssuerUrl, {
      authorizerName: `${prefix}-admin-jwt`,
      jwtAudience: [props.userPoolClientId],
      identitySource: ['$request.header.Authorization'],
    });

    // Admin: every method requires a valid Cognito token ...
    this.httpApi.addRoutes({
      path: ADMIN_ROUTE,
      methods: [apigwv2.HttpMethod.ANY],
      integration,
      authorizer: adminAuthorizer,
    });
    // ... except CORS preflight, which browsers send without an Authorization header.
    // A method-specific route wins over ANY, so OPTIONS reaches the application's CORS middleware.
    const preflightRoutes = this.httpApi.addRoutes({
      path: ADMIN_ROUTE,
      methods: [apigwv2.HttpMethod.OPTIONS],
      integration,
    });
    // Public storefront API, health check, Swagger.
    const publicRoutes = this.httpApi.addRoutes({
      path: PUBLIC_ROUTE,
      methods: [apigwv2.HttpMethod.ANY],
      integration,
    });
    // Explicit routes for the endpoints that get stricter throttling below. Same integration, no authorizer; the
    // application does its own per-IP rate limiting on both. (OPTIONS on these paths still reaches ANY /{proxy+}.)
    const throttledRoutes = [TRACKING_LOOKUP_ROUTE_PATH, CREATE_ORDER_ROUTE_PATH].flatMap((path) =>
      this.httpApi.addRoutes({ path, methods: [apigwv2.HttpMethod.POST], integration }),
    );
    this.unauthenticatedRoutes = [...preflightRoutes, ...publicRoutes, ...throttledRoutes];

    const accessLogGroup = new logs.LogGroup(this, 'HttpApiAccessLogs', {
      logGroupName: `/aws/apigateway/${prefix}-api-access`,
      retention: config.api.logRetentionDays as logs.RetentionDays,
      removalPolicy: config.isProduction ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });

    const customDomain = config.domains.api;
    const domainName = customDomain
      ? new apigwv2.DomainName(this, 'ApiDomainName', {
          domainName: customDomain.domainName,
          certificate: acm.Certificate.fromCertificateArn(this, 'ApiCertificate', customDomain.certificateArn),
        })
      : undefined;

    this.stage = new apigwv2.HttpStage(this, 'DefaultStage', {
      httpApi: this.httpApi,
      stageName: '$default',
      autoDeploy: true,
      throttle: {
        rateLimit: config.api.throttleRateLimit,
        burstLimit: config.api.throttleBurstLimit,
      },
      accessLogSettings: {
        destination: new apigwv2.LogGroupLogDestination(accessLogGroup),
        // No query string and no Authorization header: tracking codes and tokens must never be logged.
        format: AccessLogFormat.custom(
          JSON.stringify({
            requestId: '$context.requestId',
            requestTime: '$context.requestTime',
            ip: '$context.identity.sourceIp',
            httpMethod: '$context.httpMethod',
            routeKey: '$context.routeKey',
            path: '$context.path',
            status: '$context.status',
            protocol: '$context.protocol',
            responseLength: '$context.responseLength',
            responseLatency: '$context.responseLatency',
            integrationLatency: '$context.integrationLatency',
            integrationError: '$context.integrationErrorMessage',
            authorizerError: '$context.authorizer.error',
            userAgent: '$context.identity.userAgent',
          }),
        ),
      },
      ...(domainName ? { domainMapping: { domainName } } : {}),
    });

    // Per-route throttling (the L2 HttpStage only exposes the default route settings). `RouteSettings` is a raw JSON
    // map in CloudFormation, so the property names are written in CloudFormation (PascalCase) form.
    const routeThrottle = (throttle: ThrottleConfig) => ({
      ThrottlingRateLimit: throttle.rateLimit,
      ThrottlingBurstLimit: throttle.burstLimit,
    });
    const cfnStage = this.stage.node.defaultChild as apigwv2.CfnStage;
    cfnStage.routeSettings = {
      [TRACKING_LOOKUP_ROUTE_KEY]: routeThrottle(config.api.routeThrottling.trackingLookup),
      [CREATE_ORDER_ROUTE_KEY]: routeThrottle(config.api.routeThrottling.createOrder),
    };
    // Route settings refer to the routes by key; the routes must exist first.
    for (const route of throttledRoutes) this.stage.node.addDependency(route);

    this.apiUrl = customDomain ? `https://${customDomain.domainName}` : this.httpApi.apiEndpoint;

    new CfnOutput(this, 'ApiUrl', { value: this.apiUrl, description: 'Base URL of the HTTP API' });
    new CfnOutput(this, 'ApiFunctionName', { value: this.apiFunction.function.functionName });
    new CfnOutput(this, 'OutboxWorkerFunctionName', {
      value: this.outboxWorker.function.functionName,
      description: 'E-mail outbox worker (EventBridge, every minute)',
    });
    if (domainName) {
      new CfnOutput(this, 'ApiDomainTarget', {
        value: domainName.regionalDomainName,
        description: 'Create a CNAME/ALIAS DNS record from the API domain to this target',
      });
    }
  }

  /** The outbox worker: same package as the migration runner, in the VPC, the app database role. */
  private createOutboxWorker(
    props: ApiStackProps,
    databaseEnvironment: Record<string, string>,
    trackingEnvironment: Record<string, string>,
    readTrackingKey: iam.PolicyStatement,
  ): DotnetFunction {
    const { config, database, notifications } = props;
    const email = config.email;
    const worker = config.outboxWorker;

    const optional = (key: string, value: string | number | undefined): Record<string, string> =>
      value === undefined ? {} : { [key]: String(value) };
    const environment: Record<string, string> = {
      ASPNETCORE_ENVIRONMENT: config.api.aspnetEnvironment,
      DOTNET_ENVIRONMENT: config.api.aspnetEnvironment,
      ...databaseEnvironment,
      ...trackingEnvironment,
      Email__Enabled: email.enabled ? 'true' : 'false',
      Email__Provider: 'Ses',
      Email__Ses__Region: this.region,
      ...optional('Email__FromAddress', email.fromAddress),
      ...optional('Email__FromName', email.fromName),
      ...optional('Email__ReplyToAddress', email.replyToAddress),
      ...optional('Email__LogoUrl', email.logoUrl),
      ...optional('Email__Ses__ConfigurationSetName', notifications.configurationSetName),
      ...optional('Email__Dispatcher__BatchSize', worker.batchSize),
    };

    const statements: iam.PolicyStatement[] = [database.appConnectStatement(config.database.appUser), readTrackingKey];
    if (email.enabled) {
      // validateConfig guarantees an identity (and therefore a configuration set) and a sender address.
      // SES v2 SendEmail is authorised against the sending identity and the configuration set (and, in the sandbox,
      // the verified recipient identities). The condition pins the sender to this environment's address, which
      // matters because staging and prod share the domain identity.
      const recipientArns = email.sandboxRecipients.map((address) =>
        this.formatArn({ service: 'ses', resource: 'identity', resourceName: address, arnFormat: ArnFormat.SLASH_RESOURCE_NAME }),
      );
      statements.push(
        new iam.PolicyStatement({
          sid: 'SendEmailAsEnvironmentSender',
          actions: ['ses:SendEmail'],
          resources: [notifications.emailIdentityArn!, notifications.configurationSetArn!, ...recipientArns],
          conditions: { StringEquals: { 'ses:FromAddress': email.fromAddress! } },
        }),
      );
    }

    return new DotnetFunction(this, 'OutboxWorker', {
      functionName: `${resourcePrefix(config)}-outbox-worker`,
      description: `Zladovce ${config.envName} e-mail outbox worker (EventBridge schedule, every minute)`,
      handler: worker.handler,
      assetPath: config.api.lambdaAssetPath,
      requireArtifacts: props.requireArtifacts,
      memoryMb: worker.memoryMb,
      timeout: Duration.seconds(worker.timeoutSeconds),
      // One run at a time is enough (and keeps database connections low); parallel runs would be safe anyway (rows are
      // claimed with FOR UPDATE SKIP LOCKED). Skipped when reservations are disabled for the account.
      reservedConcurrency: config.api.reservedConcurrency === undefined ? undefined : 1,
      logRetentionDays: config.api.logRetentionDays,
      retainLogs: config.isProduction,
      vpc: props.vpc,
      securityGroup: props.lambdaSecurityGroup,
      environment,
      policyStatements: statements,
      // Scheduled (asynchronous) invocations: a failed or throttled run is not retried; the next minute's run is.
      retryAttempts: 0,
      maxEventAge: Duration.minutes(1),
    });
  }
}

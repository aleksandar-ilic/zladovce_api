import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import { AccessLogFormat } from 'aws-cdk-lib/aws-apigateway';
import * as apigwv2 from 'aws-cdk-lib/aws-apigatewayv2';
import { HttpJwtAuthorizer } from 'aws-cdk-lib/aws-apigatewayv2-authorizers';
import { HttpLambdaIntegration } from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';
import {
  allCorsOrigins,
  EnvironmentConfig,
  MEDIA_PREFIX,
  resourcePrefix,
  UPLOADS_PREFIX,
} from '../config';
import { DotnetFunction } from '../constructs/dotnet-function';
import { DatabaseStack } from './database-stack';

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
}

export const ADMIN_ROUTE = '/api/v1/admin/{proxy+}';
export const PUBLIC_ROUTE = '/{proxy+}';
/** Alias the HTTP API invokes; move it back to a previous version to roll back code. */
export const LIVE_ALIAS = 'live';

/**
 * API Lambda (ASP.NET Core) and the HTTP API in front of it.
 * The migration runner lives in MigrationStack so that the pipeline can deploy and
 * run it BEFORE this stack publishes a new API version.
 *
 * Extension points (later phases): the outbox worker is another `DotnetFunction`
 * in this stack with an EventBridge schedule; SES and Cognito admin permissions
 * are added to `apiFunction.role`.
 */
export class ApiStack extends Stack {
  public readonly apiFunction: DotnetFunction;
  public readonly apiAlias: lambda.Alias;
  public readonly httpApi: apigwv2.HttpApi;
  public readonly stage: apigwv2.HttpStage;
  /** Routes that intentionally have no authorizer (public API and admin CORS preflight). */
  public readonly unauthenticatedRoutes: apigwv2.HttpRoute[];
  /** Base URL without trailing slash. */
  public readonly apiUrl: string;

  constructor(scope: Construct, id: string, props: ApiStackProps) {
    super(scope, id, props);
    const { config, database } = props;
    const prefix = resourcePrefix(config);
    const db = config.database;

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
        Database__Host: database.appEndpointAddress,
        Database__Port: database.port,
        Database__Name: db.databaseName,
        Database__User: db.appUser,
        Database__UseIamAuth: 'true',
        Database__Region: this.region,
        Database__MaxPoolSize: String(config.api.dbMaxPoolSize),
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
    this.unauthenticatedRoutes = [...preflightRoutes, ...publicRoutes];

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

    this.apiUrl = customDomain ? `https://${customDomain.domainName}` : this.httpApi.apiEndpoint;

    new CfnOutput(this, 'ApiUrl', { value: this.apiUrl, description: 'Base URL of the HTTP API' });
    new CfnOutput(this, 'ApiFunctionName', { value: this.apiFunction.function.functionName });
    if (domainName) {
      new CfnOutput(this, 'ApiDomainTarget', {
        value: domainName.regionalDomainName,
        description: 'Create a CNAME/ALIAS DNS record from the API domain to this target',
      });
    }
  }
}

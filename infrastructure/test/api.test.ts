import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Annotations, Match } from 'aws-cdk-lib/assertions';
import { buildApp, onlyResource, synth } from './helpers';

function functionByName(template: ReturnType<typeof synth>['api'], name: string): any {
  const found = Object.values(template.findResources('AWS::Lambda::Function')).find(
    (fn: any) => fn.Properties.FunctionName === name,
  );
  if (!found) throw new Error(`Function ${name} not found`);
  return found;
}

function roleStatements(template: ReturnType<typeof synth>['api'], roleLogicalIdPrefix: string): any[] {
  const roles = template.findResources('AWS::IAM::Role');
  const id = Object.keys(roles).find((key) => key.startsWith(roleLogicalIdPrefix));
  if (!id) throw new Error(`Role ${roleLogicalIdPrefix} not found`);
  return (roles[id].Properties.Policies ?? []).flatMap((policy: any) => policy.PolicyDocument.Statement);
}

describe('API Lambda', () => {
  const dev = synth('dev');
  const prod = synth('prod');
  const fn = functionByName(dev.api, 'zladovce-dev-api');

  test('.NET 10 on arm64, 1024 MB, 15 s, ASP.NET Core handler', () => {
    expect(fn.Properties).toMatchObject({
      Runtime: 'dotnet10',
      Architectures: ['arm64'],
      MemorySize: 1024,
      Timeout: 15,
      Handler: 'Zladovce.Api',
    });
  });

  test('runs in the private subnets of the VPC with the Lambda security group', () => {
    const vpcConfig = fn.Properties.VpcConfig;
    expect(vpcConfig.SubnetIds).toHaveLength(2);
    expect(JSON.stringify(vpcConfig.SubnetIds)).toContain('PrivateSubnet');
    expect(JSON.stringify(vpcConfig.SubnetIds)).not.toContain('PublicSubnet');
    expect(vpcConfig.SecurityGroupIds).toHaveLength(1);
    expect(JSON.stringify(vpcConfig.SecurityGroupIds)).toContain('LambdaSecurityGroup');
  });

  test('reserved concurrency comes from configuration (default 10)', () => {
    expect(fn.Properties.ReservedConcurrentExecutions).toBe(10);
    const custom = synth('dev', { api: { reservedConcurrency: 3 } });
    expect(functionByName(custom.api, 'zladovce-dev-api').Properties.ReservedConcurrentExecutions).toBe(3);
  });

  test('non-secret configuration is passed as environment variables', () => {
    const variables = fn.Properties.Environment.Variables;
    expect(variables).toMatchObject({
      ASPNETCORE_ENVIRONMENT: 'Dev',
      Database__Name: 'zladovce',
      Database__User: 'zladovce_app',
      Database__UseIamAuth: 'true',
      Database__Region: 'eu-central-1',
      Database__MaxPoolSize: '2',
      Auth__Cognito__Region: 'eu-central-1',
      Cors__AllowedOrigins__0: 'http://localhost:4200',
      Cors__AllowedOrigins__1: 'http://localhost:4201',
    });
    for (const key of [
      'Database__Host',
      'Database__Port',
      'Auth__Cognito__UserPoolId',
      'Auth__Cognito__ClientId',
      'Media__BucketName',
      'Media__BaseUrl',
    ]) {
      expect(variables[key]).toBeDefined();
    }
    // Uploads are processed by the image processor Lambda; attach waits up to 8 s for it.
    expect(variables).toMatchObject({
      Media__ProcessInline: 'false',
      Media__ReadyWaitSeconds: '8',
      Media__Region: 'eu-central-1',
    });
    // Optional; the backend default applies when not configured.
    expect(variables.Catalog__MaxOrderQuantity).toBeUndefined();
    // Tracking codes: storefront links and the key's secret ARN (never the key itself); no in-process dispatcher.
    expect(variables.Storefront__BaseUrl).toBe('https://dev.zladovce.com');
    expect(JSON.stringify(variables.Tracking__TokenEncryptionKeySecretArn)).toContain('TrackingKeySecret');
    expect(variables.Email__Dispatcher__RunInProcess).toBe('false');
    expect(variables.Tracking__TokenEncryptionKey).toBeUndefined();
    // Nothing that looks like a credential (secret ARNs are references, not values).
    expect(
      Object.keys(variables).some((key) => /password|secret|connectionstring/i.test(key) && !key.endsWith('SecretArn')),
    ).toBe(false);
  });

  test('optional Catalog__MaxOrderQuantity and a different ready wait come from configuration', () => {
    const custom = synth('dev', { api: { maxOrderQuantity: 12, mediaReadyWaitSeconds: 5 } });
    expect(functionByName(custom.api, 'zladovce-dev-api').Properties.Environment.Variables).toMatchObject({
      Catalog__MaxOrderQuantity: '12',
      Media__ReadyWaitSeconds: '5',
    });
  });

  test('ASPNETCORE_ENVIRONMENT is never Development in AWS', () => {
    for (const [result, name] of [
      [dev, 'zladovce-dev-api'],
      [synth('staging'), 'zladovce-staging-api'],
      [prod, 'zladovce-prod-api'],
    ] as const) {
      const value = functionByName(result.api, name).Properties.Environment.Variables.ASPNETCORE_ENVIRONMENT;
      expect(value).not.toBe('Development');
    }
    expect(functionByName(prod.api, 'zladovce-prod-api').Properties.Environment.Variables.ASPNETCORE_ENVIRONMENT).toBe(
      'Production',
    );
  });

  test('execution role: no managed policies, database access as the app user only, S3 limited to prefixes', () => {
    const roles = dev.api.findResources('AWS::IAM::Role');
    for (const role of Object.values(roles) as any[]) {
      expect(role.Properties.ManagedPolicyArns).toBeUndefined();
    }
    dev.api.resourceCountIs('AWS::IAM::Policy', 0);

    const statements = roleStatements(dev.api, 'ApiRole');

    const connect = statements.filter((s) => s.Action === 'rds-db:connect');
    expect(connect).toHaveLength(1);
    const connectResource = JSON.stringify(connect[0].Resource);
    expect(connectResource).toContain(':dbuser:');
    expect(connectResource).toContain('/zladovce_app');
    expect(connectResource).not.toContain('*');

    const s3 = statements.filter((s) => JSON.stringify(s.Action).includes('s3:'));
    expect(s3).toHaveLength(3);
    // Pre-signed POST is signed by this role: PutObject on uploads/* only.
    const put = s3.find((s) => s.Action === 's3:PutObject');
    expect(JSON.stringify(put.Resource)).toContain('/uploads/*');
    // Attach reads the manifest, deleting an image removes its renditions: media/* only.
    const read = s3.find((s) => Array.isArray(s.Action));
    expect(read.Action.sort()).toEqual(['s3:DeleteObject', 's3:GetObject']);
    expect(JSON.stringify(read.Resource)).toContain('/media/*');
    // 404 instead of 403 for a manifest that does not exist yet: ListBucket on the media bucket itself.
    const list = s3.find((s) => s.Action === 's3:ListBucket');
    expect(JSON.stringify(list.Resource)).toContain('MediaBucket');
    expect(JSON.stringify(list.Resource)).not.toContain('/');
    // The API never reads or deletes raw uploads.
    expect(s3.some((s) => JSON.stringify(s.Resource).includes('/uploads/') && s.Action !== 's3:PutObject')).toBe(false);

    // The only "*" resource is the unavoidable VPC network-interface statement.
    const wildcard = statements.filter((s) => s.Resource === '*');
    expect(wildcard).toHaveLength(1);
    expect(wildcard[0].Action.every((action: string) => action.startsWith('ec2:'))).toBe(true);

    // No wildcard actions anywhere.
    for (const statement of statements) {
      const actions: string[] = Array.isArray(statement.Action) ? statement.Action : [statement.Action];
      expect(actions.some((action) => action.includes('*'))).toBe(false);
    }
    // The only secret the API can read is the tracking-code key; it cannot send e-mail.
    const secretStatements = statements.filter((s) => JSON.stringify(s.Action).includes('secretsmanager'));
    expect(secretStatements).toHaveLength(1);
    expect(secretStatements[0].Action).toBe('secretsmanager:GetSecretValue');
    expect(JSON.stringify(secretStatements[0].Resource)).toMatch(/^\{"Fn::ImportValue":"Zladovce-Dev-Notifications:.*TrackingKeySecret.*"\}$/);
    expect(statements.some((s) => JSON.stringify(s.Action).includes('ses:'))).toBe(false);
  });

  test('log retention: 30 days outside production, 90 days in production', () => {
    dev.api.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/zladovce-dev-api',
      RetentionInDays: 30,
    });
    prod.api.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/zladovce-prod-api',
      RetentionInDays: 90,
    });
  });

  test('HTTP API invokes the "live" alias', () => {
    dev.api.hasResourceProperties('AWS::Lambda::Alias', { Name: 'live' });
    const { resource } = onlyResource(dev.api, 'AWS::ApiGatewayV2::Integration');
    expect(resource.Properties.IntegrationType).toBe('AWS_PROXY');
    expect(resource.Properties.PayloadFormatVersion).toBe('2.0');
    expect(JSON.stringify(resource.Properties.IntegrationUri)).toContain('ApiLiveAlias');
  });
});

describe('migration runner Lambda', () => {
  const dev = synth('dev');
  const fn = functionByName(dev.migrations, 'zladovce-dev-migrations');

  test('in the VPC, with the configured handler, as the migration database user', () => {
    expect(fn.Properties.Handler).toBe('Zladovce.Lambda::Zladovce.Lambda.MigrationRunner::HandleAsync');
    expect(fn.Properties.Runtime).toBe('dotnet10');
    expect(fn.Properties.VpcConfig.SubnetIds).toHaveLength(2);
    // Private subnets with egress: Secrets Manager (bootstrap) is reached through the NAT.
    expect(JSON.stringify(fn.Properties.VpcConfig.SubnetIds)).toContain('PrivateSubnet');
    expect(JSON.stringify(fn.Properties.VpcConfig.SubnetIds)).not.toContain('PublicSubnet');
    expect(fn.Properties.Environment.Variables.Database__User).toBe('zladovce_migrator');
    expect(fn.Properties.ReservedConcurrentExecutions).toBe(1);
  });

  test('may connect as the migration user only', () => {
    const statements = roleStatements(dev.migrations, 'MigrationsRole');
    const connect = statements.filter((s) => s.Action === 'rds-db:connect');
    expect(connect).toHaveLength(1);
    expect(JSON.stringify(connect[0].Resource)).toContain('/zladovce_migrator');
    expect(statements.some((s) => JSON.stringify(s.Action).includes('s3:'))).toBe(false);
  });

  test('master secret access is a switch (and tells the pipeline whether to run the bootstrap)', () => {
    const withAccess = roleStatements(dev.migrations, 'MigrationsRole');
    const read = withAccess.filter((s) => s.Action === 'secretsmanager:GetSecretValue');
    expect(read).toHaveLength(1);
    // Exactly the master secret of the database stack, imported across stacks.
    expect(JSON.stringify(read[0].Resource)).toContain('Fn::ImportValue');
    expect(JSON.stringify(read[0].Resource)).toContain('Postgres');
    expect(fn.Properties.Environment.Variables.Database__MasterSecretArn).toBeDefined();
    dev.migrations.hasOutput('MigrationBootstrapEnabled', { Value: 'true' });

    const without = synth('dev', { database: { migrationRunnerCanReadMasterSecret: false } });
    const statements = roleStatements(without.migrations, 'MigrationsRole');
    expect(statements.some((s) => JSON.stringify(s.Action).includes('secretsmanager'))).toBe(false);
    expect(
      functionByName(without.migrations, 'zladovce-dev-migrations').Properties.Environment.Variables.Database__MasterSecretArn,
    ).toBeUndefined();
    without.migrations.hasOutput('MigrationBootstrapEnabled', { Value: 'false' });
  });

  test('is not reachable through API Gateway', () => {
    const { resource } = onlyResource(dev.api, 'AWS::ApiGatewayV2::Integration');
    expect(JSON.stringify(resource.Properties.IntegrationUri)).not.toContain('Migrations');
    dev.migrations.resourceCountIs('AWS::Lambda::Permission', 0);
    dev.migrations.resourceCountIs('AWS::Lambda::Url', 0);
    // The API stack contains the API function and the outbox worker only.
    dev.api.resourceCountIs('AWS::Lambda::Function', 2);
  });
});

describe('HTTP API', () => {
  const dev = synth('dev');
  const routes = Object.values(dev.api.findResources('AWS::ApiGatewayV2::Route')) as any[];
  const route = (key: string): any => routes.find((r) => r.Properties.RouteKey === key);

  test('admin routes require the Cognito JWT authorizer', () => {
    const admin = route('ANY /api/v1/admin/{proxy+}');
    expect(admin).toBeDefined();
    expect(admin.Properties.AuthorizationType).toBe('JWT');
    expect(admin.Properties.AuthorizerId).toBeDefined();

    const { resource: authorizer } = onlyResource(dev.api, 'AWS::ApiGatewayV2::Authorizer');
    expect(authorizer.Properties.AuthorizerType).toBe('JWT');
    expect(authorizer.Properties.IdentitySource).toEqual(['$request.header.Authorization']);
    const issuer = JSON.stringify(authorizer.Properties.JwtConfiguration.Issuer);
    expect(issuer).toContain('https://cognito-idp.eu-central-1.amazonaws.com/');
    expect(issuer).toContain('AdminUserPool');
    expect(authorizer.Properties.JwtConfiguration.Audience).toHaveLength(1);
    expect(JSON.stringify(authorizer.Properties.JwtConfiguration.Audience)).toContain('AdminSpaClient');
  });

  test('public routes have no authorizer', () => {
    const publicRoute = route('ANY /{proxy+}');
    expect(publicRoute).toBeDefined();
    expect(publicRoute.Properties.AuthorizationType).toBe('NONE');
    expect(publicRoute.Properties.AuthorizerId).toBeUndefined();
  });

  test('the other unauthenticated routes are the admin CORS preflight and the two throttled public routes', () => {
    expect(routes.map((r) => r.Properties.RouteKey).sort()).toEqual([
      'ANY /api/v1/admin/{proxy+}',
      'ANY /{proxy+}',
      'OPTIONS /api/v1/admin/{proxy+}',
      'POST /api/v1/orders',
      'POST /api/v1/tracking/lookup',
    ]);
    for (const key of ['OPTIONS /api/v1/admin/{proxy+}', 'POST /api/v1/orders', 'POST /api/v1/tracking/lookup']) {
      expect(route(key).Properties.AuthorizationType).toBe('NONE');
    }
    // Same integration as everything else.
    expect(route('POST /api/v1/orders').Properties.Target).toEqual(route('ANY /{proxy+}').Properties.Target);
  });

  test('per-route throttling on tracking lookup and checkout, after the routes exist', () => {
    const stages = dev.api.findResources('AWS::ApiGatewayV2::Stage');
    const [stage] = Object.values(stages) as any[];
    expect(stage.Properties.RouteSettings).toEqual({
      'POST /api/v1/tracking/lookup': { ThrottlingRateLimit: 10, ThrottlingBurstLimit: 20 },
      'POST /api/v1/orders': { ThrottlingRateLimit: 5, ThrottlingBurstLimit: 10 },
    });
    const routeIds = Object.entries(dev.api.findResources('AWS::ApiGatewayV2::Route'))
      .filter(([, r]: [string, any]) => r.Properties.RouteKey.startsWith('POST '))
      .map(([id]) => id);
    expect(routeIds).toHaveLength(2);
    for (const id of routeIds) expect(stage.DependsOn).toContain(id);

    const custom = synth('dev', {
      api: { routeThrottling: { trackingLookup: { rateLimit: 2, burstLimit: 4 }, createOrder: { rateLimit: 1, burstLimit: 2 } } },
    });
    custom.api.hasResourceProperties('AWS::ApiGatewayV2::Stage', {
      RouteSettings: {
        'POST /api/v1/tracking/lookup': { ThrottlingRateLimit: 2, ThrottlingBurstLimit: 4 },
        'POST /api/v1/orders': { ThrottlingRateLimit: 1, ThrottlingBurstLimit: 2 },
      },
    });
  });

  test('no gateway-level CORS (handled by the application)', () => {
    const { resource } = onlyResource(dev.api, 'AWS::ApiGatewayV2::Api');
    expect(resource.Properties.ProtocolType).toBe('HTTP');
    expect(resource.Properties.CorsConfiguration).toBeUndefined();
  });

  test('default throttling and access logging', () => {
    dev.api.hasResourceProperties('AWS::ApiGatewayV2::Stage', {
      StageName: '$default',
      AutoDeploy: true,
      DefaultRouteSettings: { ThrottlingRateLimit: 20, ThrottlingBurstLimit: 40 },
      AccessLogSettings: { DestinationArn: Match.anyValue(), Format: Match.stringLikeRegexp('requestId') },
    });
    synth('prod').api.hasResourceProperties('AWS::ApiGatewayV2::Stage', {
      DefaultRouteSettings: { ThrottlingRateLimit: 50, ThrottlingBurstLimit: 100 },
    });
  });

  test('no custom domain unless configured', () => {
    dev.api.resourceCountIs('AWS::ApiGatewayV2::DomainName', 0);
    const custom = synth('staging', {
      domains: {
        api: {
          domainName: 'api.example.test',
          certificateArn: 'arn:aws:acm:eu-central-1:111111111111:certificate/00000000-0000-0000-0000-000000000000',
        },
      },
    });
    custom.api.hasResourceProperties('AWS::ApiGatewayV2::DomainName', { DomainName: 'api.example.test' });
    custom.api.resourceCountIs('AWS::ApiGatewayV2::ApiMapping', 1);
  });
});

describe('Lambda artifacts', () => {
  test('missing artifacts fall back to a placeholder with a warning', () => {
    const { environment } = buildApp('dev', {
      api: { apiAssetPath: './does-not-exist/api', lambdaAssetPath: './does-not-exist/lambda' },
    });
    expect(environment.api.apiFunction.usesPlaceholderCode).toBe(true);
    expect(environment.media.imageProcessor.usesPlaceholderCode).toBe(true);
    Annotations.fromStack(environment.api).hasWarning('*', Match.stringLikeRegexp('PLACEHOLDER LAMBDA CODE'));
    Annotations.fromStack(environment.media).hasWarning('*', Match.stringLikeRegexp('PLACEHOLDER LAMBDA CODE'));
    const fn = functionByName(
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      require('aws-cdk-lib/assertions').Template.fromStack(environment.api),
      'zladovce-dev-api',
    );
    expect(fn.Properties.Description).toContain('PLACEHOLDER');
  });

  test('requireArtifacts=true fails when an artifact is missing', () => {
    expect(() =>
      buildApp(
        'dev',
        { api: { apiAssetPath: './does-not-exist/api', lambdaAssetPath: './does-not-exist/lambda' } },
        { requireArtifacts: true },
      ),
    ).toThrow(/requireArtifacts=true/);
  });

  test('a real artifact directory is used when present', () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'zladovce-artifact-'));
    fs.writeFileSync(path.join(directory, 'Zladovce.Api.dll'), 'not a real assembly');
    try {
      const { environment } = buildApp(
        'dev',
        { api: { apiAssetPath: directory, lambdaAssetPath: directory } },
        { requireArtifacts: true },
      );
      expect(environment.api.apiFunction.usesPlaceholderCode).toBe(false);
      // One Zladovce.Lambda package for every non-HTTP handler.
      expect(environment.migrations.migrationFunction.usesPlaceholderCode).toBe(false);
      expect(environment.media.imageProcessor.usesPlaceholderCode).toBe(false);
      expect(environment.api.outboxWorker.usesPlaceholderCode).toBe(false);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });
});

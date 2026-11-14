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
    // Nothing that looks like a credential.
    expect(Object.keys(variables).some((key) => /password|secret|connectionstring/i.test(key))).toBe(false);
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
    expect(s3).toHaveLength(2);
    const put = s3.find((s) => s.Action === 's3:PutObject');
    expect(JSON.stringify(put.Resource)).toContain('/uploads/*');
    const read = s3.find((s) => Array.isArray(s.Action));
    expect(read.Action.sort()).toEqual(['s3:DeleteObject', 's3:GetObject']);
    expect(JSON.stringify(read.Resource)).toContain('/media/*');

    // The only "*" resource is the unavoidable VPC network-interface statement.
    const wildcard = statements.filter((s) => s.Resource === '*');
    expect(wildcard).toHaveLength(1);
    expect(wildcard[0].Action.every((action: string) => action.startsWith('ec2:'))).toBe(true);

    // No wildcard actions anywhere.
    for (const statement of statements) {
      const actions: string[] = Array.isArray(statement.Action) ? statement.Action : [statement.Action];
      expect(actions.some((action) => action.includes('*'))).toBe(false);
    }
    // The API cannot read any secret.
    expect(statements.some((s) => JSON.stringify(s.Action).includes('secretsmanager'))).toBe(false);
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

  test('master secret access is a switch', () => {
    const withAccess = roleStatements(dev.migrations, 'MigrationsRole');
    expect(withAccess.filter((s) => s.Action === 'secretsmanager:GetSecretValue')).toHaveLength(1);

    const without = synth('dev', { database: { migrationRunnerCanReadMasterSecret: false } });
    const statements = roleStatements(without.migrations, 'MigrationsRole');
    expect(statements.some((s) => JSON.stringify(s.Action).includes('secretsmanager'))).toBe(false);
    expect(
      functionByName(without.migrations, 'zladovce-dev-migrations').Properties.Environment.Variables.Database__MasterSecretArn,
    ).toBeUndefined();
  });

  test('is not reachable through API Gateway', () => {
    const { resource } = onlyResource(dev.api, 'AWS::ApiGatewayV2::Integration');
    expect(JSON.stringify(resource.Properties.IntegrationUri)).not.toContain('Migrations');
    dev.migrations.resourceCountIs('AWS::Lambda::Permission', 0);
    dev.migrations.resourceCountIs('AWS::Lambda::Url', 0);
    // The API stack contains the API function only.
    dev.api.resourceCountIs('AWS::Lambda::Function', 1);
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

  test('the only other unauthenticated route is the admin CORS preflight', () => {
    expect(routes.map((r) => r.Properties.RouteKey).sort()).toEqual([
      'ANY /api/v1/admin/{proxy+}',
      'ANY /{proxy+}',
      'OPTIONS /api/v1/admin/{proxy+}',
    ]);
    expect(route('OPTIONS /api/v1/admin/{proxy+}').Properties.AuthorizationType).toBe('NONE');
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
      api: { apiAssetPath: './does-not-exist/api', migrationAssetPath: './does-not-exist/lambda' },
    });
    expect(environment.api.apiFunction.usesPlaceholderCode).toBe(true);
    Annotations.fromStack(environment.api).hasWarning('*', Match.stringLikeRegexp('PLACEHOLDER LAMBDA CODE'));
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
        { api: { apiAssetPath: './does-not-exist/api', migrationAssetPath: './does-not-exist/lambda' } },
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
        { api: { apiAssetPath: directory, migrationAssetPath: directory } },
        { requireArtifacts: true },
      );
      expect(environment.api.apiFunction.usesPlaceholderCode).toBe(false);
      expect(environment.migrations.migrationFunction.usesPlaceholderCode).toBe(false);
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });
});

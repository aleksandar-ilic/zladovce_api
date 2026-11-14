import * as fs from 'fs';
import { App, Validations } from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { AwsSolutionsChecks } from 'cdk-nag';
import {
  applyContextOverrides,
  ConfigOverrides,
  ENV_NAMES,
  EnvName,
  getEnvironmentConfig,
  validateConfig,
  withOverrides,
} from '../lib/config';
import { AMPLIFY_TEST_OVERRIDES, buildApp, CDK_CONTEXT, onlyResource, synth } from './helpers';

describe('observability', () => {
  const dev = synth('dev');
  const alarms = Object.values(dev.observability.findResources('AWS::CloudWatch::Alarm')) as any[];
  const alarm = (suffix: string): any => alarms.find((a) => a.Properties.AlarmName === `zladovce-dev-${suffix}`);

  test('alarm topic, without subscription unless an e-mail is configured', () => {
    dev.observability.resourceCountIs('AWS::SNS::Topic', 1);
    dev.observability.resourceCountIs('AWS::SNS::Subscription', 0);
    const withEmail = synth('dev', { observability: { alarmEmail: 'alarms@example.test' } });
    withEmail.observability.hasResourceProperties('AWS::SNS::Subscription', {
      Protocol: 'email',
      Endpoint: 'alarms@example.test',
    });
  });

  test('alarms for API 5xx, Lambda errors/throttles, RDS and the NAT instance', () => {
    expect(alarm('Api5xx').Properties).toMatchObject({ Namespace: 'AWS/ApiGateway', MetricName: '5xx' });
    expect(alarm('ApiLatencyP95').Properties).toMatchObject({ MetricName: 'Latency', ExtendedStatistic: 'p95' });
    expect(alarm('ApiLambdaErrors').Properties).toMatchObject({ Namespace: 'AWS/Lambda', MetricName: 'Errors' });
    expect(alarm('ApiLambdaThrottles').Properties).toMatchObject({ MetricName: 'Throttles' });
    expect(alarm('MigrationLambdaErrors').Properties).toMatchObject({ MetricName: 'Errors' });
    expect(alarm('RdsCpuHigh').Properties).toMatchObject({ Namespace: 'AWS/RDS', MetricName: 'CPUUtilization' });
    expect(alarm('RdsFreeStorageLow').Properties).toMatchObject({
      MetricName: 'FreeStorageSpace',
      ComparisonOperator: 'LessThanThreshold',
    });
    expect(alarm('RdsConnectionsHigh').Properties).toMatchObject({ MetricName: 'DatabaseConnections' });
    expect(alarm('NatInstanceStatusCheck1').Properties).toMatchObject({
      Namespace: 'AWS/EC2',
      MetricName: 'StatusCheckFailed',
      TreatMissingData: 'breaching',
    });
    expect(alarms).toHaveLength(9);
  });

  test('every alarm notifies the topic', () => {
    for (const a of alarms) {
      expect(a.Properties.AlarmActions).toHaveLength(1);
      expect(a.Properties.OKActions).toHaveLength(1);
    }
  });
});

describe('tags, outputs and stack settings', () => {
  const dev = synth('dev');
  const prod = synth('prod');

  test('resources are tagged with Project and Environment', () => {
    const expectTags = (resource: any, environment: string): void => {
      expect(resource.Properties.Tags).toEqual(
        expect.arrayContaining([
          { Key: 'Project', Value: 'Zladovce' },
          { Key: 'Environment', Value: environment },
        ]),
      );
    };
    expectTags(onlyResource(dev.network, 'AWS::EC2::VPC').resource, 'dev');
    expectTags(onlyResource(dev.database, 'AWS::RDS::DBInstance').resource, 'dev');
    expectTags(onlyResource(dev.media, 'AWS::S3::Bucket').resource, 'dev');
    expectTags(onlyResource(prod.media, 'AWS::CloudFront::Distribution').resource, 'prod');
    expectTags(onlyResource(prod.observability, 'AWS::SNS::Topic').resource, 'prod');
    for (const fn of [
      ...Object.values(prod.api.findResources('AWS::Lambda::Function')),
      ...Object.values(prod.migrations.findResources('AWS::Lambda::Function')),
    ] as any[]) {
      expectTags(fn, 'prod');
    }
    expect(onlyResource(prod.auth, 'AWS::Cognito::UserPool').resource.Properties.UserPoolTags).toMatchObject({
      Project: 'Zladovce',
      Environment: 'prod',
    });
  });

  test('outputs: API URL, user pool id, client id, media bucket, CloudFront domain, database endpoint', () => {
    dev.api.hasOutput('ApiUrl', {});
    dev.migrations.hasOutput('MigrationFunctionName', {});
    dev.auth.hasOutput('UserPoolId', {});
    dev.auth.hasOutput('UserPoolClientId', {});
    dev.media.hasOutput('MediaBucketName', {});
    dev.media.hasOutput('MediaDistributionDomainName', {});
    dev.database.hasOutput('DatabaseEndpoint', {});
  });

  test('stack names and termination protection', () => {
    expect(dev.environment.api.stackName).toBe('Zladovce-Dev-Api');
    expect(prod.environment.database.stackName).toBe('Zladovce-Prod-Database');
    expect(prod.environment.database.terminationProtection).toBe(true);
    expect(dev.environment.database.terminationProtection).toBe(false);
  });

  test('synthesis is environment-agnostic (no account needed) and needs no context lookups', () => {
    for (const envName of ENV_NAMES) {
      const { app } = buildApp(envName);
      const assembly = app.synth();
      expect(assembly.manifest.missing ?? []).toEqual([]);
      expect(assembly.stacks.map((stack) => stack.stackName)).toHaveLength(7);
    }
  });

  test('no stack depends on a stack "above" it (no cycles)', () => {
    const { app } = buildApp('dev', { database: { useRdsProxy: true }, ...AMPLIFY_TEST_OVERRIDES });
    const assembly = app.synth();
    const order = assembly.stacks.map((stack) => stack.stackName);
    for (const stack of assembly.stacks) {
      for (const dependency of stack.dependencies) {
        expect(order.indexOf(dependency.id)).toBeLessThan(order.indexOf(stack.stackName));
      }
    }
  });
});

describe('frontend hosting (Amplify)', () => {
  test('disabled by default: no frontend stack', () => {
    for (const envName of ENV_NAMES) {
      expect(synth(envName).frontend).toBeUndefined();
    }
  });

  test('enabling it without repository settings is rejected', () => {
    expect(() => buildApp('dev', { amplify: { enabled: true } })).toThrow(/amplify\.repository/);
  });

  describe('when enabled', () => {
    const frontend = synth('staging', AMPLIFY_TEST_OVERRIDES).frontend as Template;
    const apps = Object.values(frontend.findResources('AWS::Amplify::App')) as any[];
    const app = (name: string): any => apps.find((a) => a.Properties.Name === name);

    test('two apps (storefront, admin) built from the monorepo root "frontend"', () => {
      expect(apps).toHaveLength(2);
      frontend.resourceCountIs('AWS::Amplify::Branch', 2);
      for (const name of ['zladovce-staging-storefront', 'zladovce-staging-admin']) {
        const properties = app(name).Properties;
        expect(properties.Repository).toBe('https://git.example.invalid/owner/repository');
        expect(properties.EnvironmentVariables).toEqual(
          expect.arrayContaining([{ Name: 'AMPLIFY_MONOREPO_APP_ROOT', Value: 'frontend' }]),
        );
        expect(properties.BuildSpec).toContain('appRoot: frontend');
      }
      expect(app('zladovce-staging-storefront').Properties.BuildSpec).toContain('npm run build:storefront');
      expect(app('zladovce-staging-admin').Properties.BuildSpec).toContain('npm run build:admin');
    });

    test('access token is a Secrets Manager dynamic reference, never a literal', () => {
      for (const a of apps) {
        expect(JSON.stringify(a.Properties.AccessToken)).toContain('{{resolve:secretsmanager:');
      }
    });

    test('admin has the SPA rewrite rule, storefront does not', () => {
      expect(app('zladovce-staging-admin').Properties.CustomRules).toEqual([
        expect.objectContaining({ Target: '/index.html', Status: '200' }),
      ]);
      expect(app('zladovce-staging-storefront').Properties.CustomRules).toBeUndefined();
    });

    test('security headers including a report-only CSP', () => {
      for (const a of apps) {
        const headers = JSON.stringify(a.Properties.CustomHeaders);
        expect(headers).toContain('Strict-Transport-Security');
        expect(headers).toContain('X-Content-Type-Options');
        expect(headers).toContain('Referrer-Policy');
        expect(headers).toContain('Content-Security-Policy-Report-Only');
        expect(headers).toContain("default-src 'self'");
      }
    });

    test('only public values reach the frontend build', () => {
      for (const a of apps) {
        const names: string[] = a.Properties.EnvironmentVariables.map((v: any) => v.Name);
        expect(names.some((name) => /secret|password|token|key/i.test(name))).toBe(false);
      }
    });
  });
});

describe('configuration', () => {
  test('all environments are valid and use eu-central-1', () => {
    for (const envName of ENV_NAMES) {
      const config = getEnvironmentConfig(envName);
      expect(() => validateConfig(config)).not.toThrow();
      expect(config.region).toBe('eu-central-1');
      expect(config.network.natMode).toBe('instance');
      expect(config.database.useRdsProxy).toBe(false);
      expect(config.amplify.enabled).toBe(false);
    }
  });

  test('wildcard or path CORS origins are rejected', () => {
    const config = getEnvironmentConfig('dev');
    expect(() => validateConfig(withOverrides(config, { cors: { storefrontOrigins: ['*'] } }))).toThrow(/CORS origin/);
    expect(() =>
      validateConfig(withOverrides(config, { cors: { adminOrigins: ['https://admin.example.test/'] } })),
    ).toThrow(/CORS origin/);
  });

  test('context overrides: natMode, useRdsProxy, asset paths, alarm e-mail', () => {
    const app = new App({
      context: {
        natMode: 'gateway',
        useRdsProxy: 'true',
        apiAssetPath: '/tmp/api',
        migrationAssetPath: '/tmp/lambda',
        alarmEmail: 'alarms@example.test',
        account: '123456789012',
      },
    });
    const config = applyContextOverrides(getEnvironmentConfig('staging'), app.node);
    expect(config.network.natMode).toBe('gateway');
    expect(config.database.useRdsProxy).toBe(true);
    expect(config.api.apiAssetPath).toBe('/tmp/api');
    expect(config.api.migrationAssetPath).toBe('/tmp/lambda');
    expect(config.observability.alarmEmail).toBe('alarms@example.test');
    expect(config.account).toBe('123456789012');
    // untouched values survive
    expect(config.database.backupRetentionDays).toBe(1);
  });

  test('invalid context values are rejected', () => {
    expect(() =>
      applyContextOverrides(getEnvironmentConfig('dev'), new App({ context: { natMode: 'none' } }).node),
    ).toThrow(/natMode/);
    expect(() =>
      applyContextOverrides(getEnvironmentConfig('dev'), new App({ context: { useRdsProxy: 'yes' } }).node),
    ).toThrow(/useRdsProxy/);
  });
});

describe('cdk-nag (AwsSolutions)', () => {
  const variants: Array<[string, EnvName, ConfigOverrides]> = [
    ['dev', 'dev', {}],
    ['staging', 'staging', {}],
    ['prod', 'prod', {}],
    ['dev with NAT gateway and RDS Proxy', 'dev', { network: { natMode: 'gateway' }, database: { useRdsProxy: true } }],
    ['prod with Amplify enabled', 'prod', AMPLIFY_TEST_OVERRIDES],
  ];

  test.each(variants)('%s: no finding without a written acknowledgment', (_name, envName, overrides) => {
    const app = new App({ context: CDK_CONTEXT });
    const config = withOverrides(getEnvironmentConfig(envName), overrides);
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    require('../lib/zladovce-environment').createZladovceEnvironment(app, config);
    Validations.of(app).addPlugins(new AwsSolutionsChecks(app));
    // Synthesis throws 'Validation failed' when a rule is violated and not acknowledged
    // (see lib/nag-suppressions.ts). Re-throw with the report so the finding is visible in the test output.
    try {
      app.synth();
    } catch (error) {
      const reportPath = /found in: (.+validation-report.json)/.exec(String(error))?.[1];
      const report = reportPath && fs.existsSync(reportPath) ? fs.readFileSync(reportPath, 'utf8') : '';
      throw new Error(`${String(error)}
${report.slice(0, 8000)}`);
    }
  });
});

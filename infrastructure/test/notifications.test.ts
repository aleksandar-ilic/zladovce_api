import * as fs from 'fs';
import * as path from 'path';
import { Match, Template } from 'aws-cdk-lib/assertions';
import {
  ENV_NAMES,
  getEnvironmentConfig,
  OUTBOX_DEAD_LOG_TEXT,
  TRACKING_KEY_PLACEHOLDER_MARKER,
  validateConfig,
  withOverrides,
} from '../lib/config';
import { synth } from './helpers';

function functionByName(template: Template, name: string): any {
  const found = Object.values(template.findResources('AWS::Lambda::Function')).find(
    (fn: any) => fn.Properties.FunctionName === name,
  );
  if (!found) throw new Error(`Function ${name} not found`);
  return found;
}

function roleStatements(template: Template, roleLogicalIdPrefix: string): any[] {
  const roles = template.findResources('AWS::IAM::Role');
  const id = Object.keys(roles).find((key) => key.startsWith(roleLogicalIdPrefix));
  if (!id) throw new Error(`Role ${roleLogicalIdPrefix} not found`);
  return (roles[id].Properties.Policies ?? []).flatMap((policy: any) => policy.PolicyDocument.Statement);
}

const actionsOf = (statement: any): string[] => (Array.isArray(statement.Action) ? statement.Action : [statement.Action]);

describe('outbox worker Lambda', () => {
  const dev = synth('dev');
  const staging = synth('staging');
  const prod = synth('prod');
  const devWorker = functionByName(dev.api, 'zladovce-dev-outbox-worker');
  const prodWorker = functionByName(prod.api, 'zladovce-prod-outbox-worker');

  test('handler from the Zladovce.Lambda package, .NET 10 arm64, 512 MB, 90 s, reserved concurrency 1', () => {
    expect(prodWorker.Properties).toMatchObject({
      Handler: 'Zladovce.Lambda::Zladovce.Lambda.OutboxWorkerFunction::HandleAsync',
      Runtime: 'dotnet10',
      Architectures: ['arm64'],
      MemorySize: 512,
      Timeout: 90,
      ReservedConcurrentExecutions: 1,
    });
    // Same package (asset) as the migration runner.
    const migration = functionByName(prod.migrations, 'zladovce-prod-migrations');
    expect(prodWorker.Properties.Code.S3Key).toBe(migration.Properties.Code.S3Key);
    // No reservation when reservations are switched off for the account.
    const noReservations = synth('dev', { api: { reservedConcurrency: undefined } });
    expect(
      functionByName(noReservations.api, 'zladovce-dev-outbox-worker').Properties.ReservedConcurrentExecutions,
    ).toBeUndefined();
  });

  test('runs in the private subnets with the Lambda security group (database, NAT to SES / Secrets Manager)', () => {
    const vpcConfig = devWorker.Properties.VpcConfig;
    expect(vpcConfig.SubnetIds).toHaveLength(2);
    expect(JSON.stringify(vpcConfig.SubnetIds)).toContain('PrivateSubnet');
    expect(JSON.stringify(vpcConfig.SubnetIds)).not.toContain('PublicSubnet');
    expect(JSON.stringify(vpcConfig.SecurityGroupIds)).toContain('LambdaSecurityGroup');
  });

  test('environment: app database role, storefront, tracking key ARN, SES settings of the environment', () => {
    const variables = prodWorker.Properties.Environment.Variables;
    expect(variables).toMatchObject({
      ASPNETCORE_ENVIRONMENT: 'Production',
      DOTNET_ENVIRONMENT: 'Production',
      Database__User: 'zladovce_app',
      Database__UseIamAuth: 'true',
      Database__Name: 'zladovce',
      Storefront__BaseUrl: 'https://zladovce.com',
      Email__Enabled: 'true',
      Email__Provider: 'Ses',
      Email__FromAddress: 'porudzbine@zladovce.com',
      Email__Ses__ConfigurationSetName: 'zladovce-prod-email',
      Email__Ses__Region: 'eu-central-1',
      Email__Dispatcher__RunInProcess: 'false',
    });
    expect(JSON.stringify(variables.Tracking__TokenEncryptionKeySecretArn)).toContain('TrackingKeySecret');
    expect(variables.Database__Host).toBeDefined();
    // Optional values are only set when configured.
    for (const key of ['Email__FromName', 'Email__ReplyToAddress', 'Email__LogoUrl', 'Email__Dispatcher__BatchSize']) {
      expect(variables[key]).toBeUndefined();
    }
    expect(functionByName(staging.api, 'zladovce-staging-outbox-worker').Properties.Environment.Variables).toMatchObject({
      Email__FromAddress: 'staging-porudzbine@zladovce.com',
      Email__FromName: 'Zladovce (staging)',
      Email__Ses__ConfigurationSetName: 'zladovce-staging-email',
      Storefront__BaseUrl: 'https://staging.zladovce.com',
    });

    const custom = synth('prod', {
      email: {
        ...getEnvironmentConfig('prod').email,
        fromName: 'Zladovce',
        replyToAddress: 'kontakt@zladovce.com',
        logoUrl: 'https://media.zladovce.com/logo.png',
      },
      outboxWorker: { batchSize: 20 },
    });
    expect(functionByName(custom.api, 'zladovce-prod-outbox-worker').Properties.Environment.Variables).toMatchObject({
      Email__FromName: 'Zladovce',
      Email__ReplyToAddress: 'kontakt@zladovce.com',
      Email__LogoUrl: 'https://media.zladovce.com/logo.png',
      Email__Dispatcher__BatchSize: '20',
    });
  });

  test('e-mail disabled (dev): worker still deployed and scheduled, sends nothing, no SES settings', () => {
    const variables = devWorker.Properties.Environment.Variables;
    expect(variables.Email__Enabled).toBe('false');
    expect(variables.Email__FromAddress).toBeUndefined();
    expect(variables.Email__Ses__ConfigurationSetName).toBeUndefined();
    dev.api.resourceCountIs('AWS::Events::Rule', 1);
  });

  test('EventBridge schedule every minute, no retries; asynchronous invocations are not retried either', () => {
    for (const [result, envName] of [
      [dev, 'dev'],
      [prod, 'prod'],
    ] as const) {
      const rules = Object.values(result.api.findResources('AWS::Events::Rule')) as any[];
      expect(rules).toHaveLength(1);
      expect(rules[0].Properties).toMatchObject({
        Name: `zladovce-${envName}-outbox-worker`,
        ScheduleExpression: 'rate(1 minute)',
        State: 'ENABLED',
      });
      expect(rules[0].Properties.Targets).toHaveLength(1);
      expect(JSON.stringify(rules[0].Properties.Targets[0].Arn)).toContain('OutboxWorkerFunction');
      expect(rules[0].Properties.Targets[0].RetryPolicy).toEqual({ MaximumRetryAttempts: 0, MaximumEventAgeInSeconds: 60 });
      expect(rules[0].Properties.Targets[0].Input).toBeUndefined();
    }
    prod.api.hasResourceProperties('AWS::Lambda::Permission', {
      Action: 'lambda:InvokeFunction',
      Principal: 'events.amazonaws.com',
      FunctionName: { 'Fn::GetAtt': [Match.stringLikeRegexp('^OutboxWorkerFunction'), 'Arn'] },
    });
    prod.api.hasResourceProperties('AWS::Lambda::EventInvokeConfig', {
      FunctionName: { Ref: Match.stringLikeRegexp('^OutboxWorkerFunction') },
      MaximumRetryAttempts: 0,
      MaximumEventAgeInSeconds: 60,
    });
  });

  test('log group with the environment retention', () => {
    dev.api.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/zladovce-dev-outbox-worker',
      RetentionInDays: 30,
    });
    prod.api.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/zladovce-prod-outbox-worker',
      RetentionInDays: 90,
    });
  });
});

describe('IAM scoping (tracking key, SES)', () => {
  const dev = synth('dev');
  const staging = synth('staging');
  const prod = synth('prod');

  test('worker: app database user, the tracking key and SendEmail on its identity + configuration set only', () => {
    const statements = roleStatements(prod.api, 'OutboxWorkerRole');

    const connect = statements.filter((s) => s.Action === 'rds-db:connect');
    expect(connect).toHaveLength(1);
    expect(JSON.stringify(connect[0].Resource)).toContain('/zladovce_app');

    const secret = statements.filter((s) => JSON.stringify(s.Action).includes('secretsmanager'));
    expect(secret).toHaveLength(1);
    expect(secret[0].Action).toBe('secretsmanager:GetSecretValue');
    expect(JSON.stringify(secret[0].Resource)).toMatch(/^\{"Fn::ImportValue":"Zladovce-Prod-Notifications:.*TrackingKeySecret.*"\}$/);

    const sesStatements = statements.filter((s) => JSON.stringify(s.Action).includes('ses:'));
    expect(sesStatements).toHaveLength(1);
    const send = sesStatements[0];
    expect(send.Action).toBe('ses:SendEmail');
    const resources = JSON.stringify(send.Resource);
    expect(send.Resource).toHaveLength(2);
    expect(resources).toContain(':identity/zladovce.com');
    expect(resources).toContain(':configuration-set/zladovce-prod-email');
    expect(resources).not.toContain('*');
    expect(send.Condition).toEqual({ StringEquals: { 'ses:FromAddress': 'porudzbine@zladovce.com' } });

    // No S3, no wildcard actions; the only "*" resource is the VPC networking statement.
    expect(statements.some((s) => JSON.stringify(s.Action).includes('s3:'))).toBe(false);
    for (const statement of statements) expect(actionsOf(statement).some((a) => a.includes('*'))).toBe(false);
    const wildcard = statements.filter((s) => s.Resource === '*');
    expect(wildcard).toHaveLength(1);
    expect(wildcard[0].Action.every((action: string) => action.startsWith('ec2:'))).toBe(true);
  });

  test('staging sends through the imported prod identity, as its own sender, with its own configuration set', () => {
    const send = roleStatements(staging.api, 'OutboxWorkerRole').filter((s) => JSON.stringify(s.Action).includes('ses:'));
    expect(send).toHaveLength(1);
    expect(JSON.stringify(send[0].Resource)).toContain(':identity/zladovce.com');
    expect(JSON.stringify(send[0].Resource)).toContain(':configuration-set/zladovce-staging-email');
    expect(send[0].Condition).toEqual({ StringEquals: { 'ses:FromAddress': 'staging-porudzbine@zladovce.com' } });
  });

  test('dev (e-mail disabled): no SES permission at all', () => {
    const statements = roleStatements(dev.api, 'OutboxWorkerRole');
    expect(statements.some((s) => JSON.stringify(s.Action).includes('ses:'))).toBe(false);
    expect(statements.filter((s) => s.Action === 'secretsmanager:GetSecretValue')).toHaveLength(1);
  });

  test('sandbox recipients are added as identity ARNs', () => {
    const result = synth('staging', {
      email: { ...getEnvironmentConfig('staging').email, sandboxRecipients: ['tester@example.com'] },
    });
    const send = roleStatements(result.api, 'OutboxWorkerRole').find((s) => s.Action === 'ses:SendEmail');
    expect(send.Resource).toHaveLength(3);
    expect(JSON.stringify(send.Resource)).toContain(':identity/tester@example.com');
  });

  test('no other role in any stack may use SES, and only API + worker read the tracking key', () => {
    for (const result of [dev, staging, prod]) {
      for (const [name, template] of Object.entries({
        network: result.network,
        database: result.database,
        auth: result.auth,
        media: result.media,
        migrations: result.migrations,
        notifications: result.notifications,
        api: result.api,
        observability: result.observability,
      })) {
        const roles = template.findResources('AWS::IAM::Role');
        for (const [id, role] of Object.entries(roles) as Array<[string, any]>) {
          const statements = (role.Properties.Policies ?? []).flatMap((p: any) => p.PolicyDocument.Statement);
          const usesSes = statements.some((s: any) => actionsOf(s).some((a) => a.startsWith('ses:')));
          const readsTrackingKey = statements.some((s: any) => JSON.stringify(s.Resource ?? '').includes('TrackingKeySecret'));
          if (usesSes) expect(`${name}/${id}`).toMatch(/^api\/OutboxWorkerRole/);
          if (readsTrackingKey) expect(`${name}/${id}`).toMatch(/^api\/(ApiRole|OutboxWorkerRole)/);
        }
        // Stand-alone policies (CDK-owned custom resources) never get SES or the tracking key either.
        for (const policy of Object.values(template.findResources('AWS::IAM::Policy')) as any[]) {
          const text = JSON.stringify(policy.Properties.PolicyDocument);
          expect(text).not.toContain('ses:');
          expect(text).not.toContain('TrackingKeySecret');
        }
      }
    }
  });
});

describe('API environment for tracking codes', () => {
  test('every environment gets Storefront__BaseUrl and the secret ARN, and never runs the dispatcher in-process', () => {
    for (const envName of ENV_NAMES) {
      const result = synth(envName);
      const variables = functionByName(result.api, `zladovce-${envName}-api`).Properties.Environment.Variables;
      expect(variables.Storefront__BaseUrl).toMatch(/^https:\/\//);
      expect(JSON.stringify(variables.Tracking__TokenEncryptionKeySecretArn)).toContain('TrackingKeySecret');
      expect(variables.Email__Dispatcher__RunInProcess).not.toBe('true');
      for (const fn of Object.values(result.api.findResources('AWS::Lambda::Function')) as any[]) {
        expect(fn.Properties.Environment.Variables.Email__Dispatcher__RunInProcess).not.toBe('true');
      }
    }
  });
});

describe('notifications stack (tracking key, SES)', () => {
  const dev = synth('dev');
  const staging = synth('staging');
  const prod = synth('prod');

  test('tracking-key secret: per environment, placeholder only (no key in the template), retained in prod', () => {
    for (const [result, envName, policy] of [
      [dev, 'dev', 'Delete'],
      [staging, 'staging', 'Delete'],
      [prod, 'prod', 'Retain'],
    ] as const) {
      const secrets = result.notifications.findResources('AWS::SecretsManager::Secret');
      expect(Object.keys(secrets)).toHaveLength(1);
      const secret = Object.values(secrets)[0] as any;
      expect(secret.Properties.Name).toBe(`zladovce/${envName}/tracking-token-key`);
      expect(secret.DeletionPolicy).toBe(policy);
      expect(secret.Properties.SecretString).toBeUndefined();
      const template = JSON.parse(secret.Properties.GenerateSecretString.SecretStringTemplate);
      expect(template).toEqual({ status: TRACKING_KEY_PLACEHOLDER_MARKER });
      // The backend rejects a value without "key", so the placeholder can never be mistaken for a key.
      expect(secret.Properties.GenerateSecretString.GenerateStringKey).not.toBe('key');
      result.notifications.hasOutput('TrackingKeySecretName', { Value: `zladovce/${envName}/tracking-token-key` });
    }
  });

  test('the notifications stack has no dependencies (deployable before the API to initialise the key)', () => {
    expect(prod.environment.notifications.dependencies).toHaveLength(0);
    expect(prod.environment.api.dependencies.map((stack) => stack.stackName)).toContain('Zladovce-Prod-Notifications');
  });

  test('prod owns the domain identity: Easy DKIM 2048, custom MAIL FROM, DNS records as outputs', () => {
    prod.notifications.resourceCountIs('AWS::SES::EmailIdentity', 1);
    prod.notifications.hasResourceProperties('AWS::SES::EmailIdentity', {
      EmailIdentity: 'zladovce.com',
      DkimAttributes: { SigningEnabled: true },
      DkimSigningAttributes: { NextSigningKeyLength: 'RSA_2048_BIT' },
      MailFromAttributes: { MailFromDomain: 'mail.zladovce.com', BehaviorOnMxFailure: 'USE_DEFAULT_VALUE' },
    });
    for (const output of ['DkimRecord1', 'DkimRecord2', 'DkimRecord3', 'MailFromMxRecord', 'MailFromSpfRecord']) {
      prod.notifications.hasOutput(output, {});
    }
    prod.notifications.resourceCountIs('AWS::Route53::RecordSet', 0);
  });

  test('with a hosted zone the DKIM records are created in Route 53 instead of being outputs', () => {
    const result = synth('prod', {
      email: {
        ...getEnvironmentConfig('prod').email,
        identity: {
          mode: 'create',
          domain: 'zladovce.com',
          hostedZone: { hostedZoneId: 'Z0000000000000000000', zoneName: 'zladovce.com' },
        },
      },
    });
    result.notifications.resourceCountIs('AWS::Route53::RecordSet', 3);
    expect(Object.keys(result.notifications.findOutputs('DkimRecord1'))).toHaveLength(0);
  });

  test('staging imports the identity (creates none) but has its own configuration set; dev has neither', () => {
    staging.notifications.resourceCountIs('AWS::SES::EmailIdentity', 0);
    staging.notifications.hasOutput('EmailIdentityName', { Value: 'zladovce.com' });
    dev.notifications.resourceCountIs('AWS::SES::EmailIdentity', 0);
    dev.notifications.resourceCountIs('AWS::SES::ConfigurationSet', 0);
    dev.notifications.resourceCountIs('AWS::SES::ConfigurationSetEventDestination', 0);
  });

  test('configuration set: reputation metrics, account suppression, CloudWatch events per environment', () => {
    for (const [result, envName] of [
      [staging, 'staging'],
      [prod, 'prod'],
    ] as const) {
      result.notifications.hasResourceProperties('AWS::SES::ConfigurationSet', {
        Name: `zladovce-${envName}-email`,
        ReputationOptions: { ReputationMetricsEnabled: true },
        SendingOptions: { SendingEnabled: true },
        SuppressionOptions: { SuppressedReasons: ['BOUNCE', 'COMPLAINT'] },
      });
      result.notifications.hasResourceProperties('AWS::SES::ConfigurationSetEventDestination', {
        EventDestination: {
          Enabled: true,
          MatchingEventTypes: Match.arrayWith(['send', 'delivery', 'bounce', 'complaint', 'reject']),
          CloudWatchDestination: {
            DimensionConfigurations: [
              {
                DimensionName: 'ses:configuration-set',
                DimensionValueSource: 'messageTag',
                DefaultDimensionValue: `zladovce-${envName}-email`,
              },
            ],
          },
        },
      });
    }
  });

  test('exactly one environment creates the domain identity (one account, one identity per domain)', () => {
    const creators = ENV_NAMES.filter((envName) => getEnvironmentConfig(envName).email.identity.mode === 'create');
    expect(creators).toEqual(['prod']);
    const domains = ENV_NAMES.map((envName) => getEnvironmentConfig(envName).email.identity)
      .filter((identity) => identity.mode === 'import')
      .map((identity) => (identity as { domain: string }).domain);
    for (const domain of domains) {
      expect(domain).toBe((getEnvironmentConfig('prod').email.identity as { domain: string }).domain);
    }
  });
});

describe('observability (outbox, SES, budget)', () => {
  const dev = synth('dev');
  const prod = synth('prod');
  const alarmsOf = (template: Template): any[] => Object.values(template.findResources('AWS::CloudWatch::Alarm'));
  const alarm = (template: Template, name: string): any =>
    alarmsOf(template).find((a) => a.Properties.AlarmName === name)?.Properties;

  test('worker errors, throttles, duration near the timeout and "not running"', () => {
    expect(alarm(dev.observability, 'zladovce-dev-OutboxWorkerErrors')).toMatchObject({
      Namespace: 'AWS/Lambda',
      MetricName: 'Errors',
      Threshold: 3,
    });
    expect(alarm(dev.observability, 'zladovce-dev-OutboxWorkerThrottles')).toMatchObject({
      MetricName: 'Throttles',
      EvaluationPeriods: 3,
      DatapointsToAlarm: 3,
    });
    expect(alarm(dev.observability, 'zladovce-dev-OutboxWorkerDurationNearTimeout')).toMatchObject({
      MetricName: 'Duration',
      Statistic: 'Maximum',
      Threshold: 85000,
    });
    expect(alarm(dev.observability, 'zladovce-dev-OutboxWorkerNotRunning')).toMatchObject({
      MetricName: 'Invocations',
      ComparisonOperator: 'LessThanThreshold',
      Threshold: 1,
      Period: 900,
      TreatMissingData: 'breaching',
    });
    for (const name of ['Errors', 'Throttles', 'DurationNearTimeout', 'NotRunning']) {
      expect(JSON.stringify(alarm(dev.observability, `zladovce-dev-OutboxWorker${name}`).Dimensions)).toContain(
        'OutboxWorkerFunction',
      );
    }
  });

  test('dead e-mails: metric filter on the message the worker actually logs', () => {
    dev.observability.hasResourceProperties('AWS::Logs::MetricFilter', {
      FilterPattern: `"${OUTBOX_DEAD_LOG_TEXT}"`,
      LogGroupName: { 'Fn::ImportValue': Match.stringLikeRegexp('OutboxWorkerLogGroup') },
      MetricTransformations: [{ MetricName: 'OutboxDeadEmails', MetricNamespace: 'Zladovce/Dev', MetricValue: '1', Unit: 'Count' }],
    });
    expect(alarm(dev.observability, 'zladovce-dev-OutboxDeadEmails')).toMatchObject({
      Namespace: 'Zladovce/Dev',
      MetricName: 'OutboxDeadEmails',
      Threshold: 1,
    });

    // Guard against the backend changing the log text (skipped if the backend is not checked out next to this).
    const dispatcher = path.join(
      __dirname,
      '..',
      '..',
      'backend',
      'src',
      'Zladovce.Infrastructure',
      'Notifications',
      'EmailDispatcher.cs',
    );
    if (fs.existsSync(dispatcher)) {
      const source = fs.readFileSync(dispatcher, 'utf8');
      expect(source).toContain('attempt {Attempt} failed; now {Status}.');
      expect(source).toContain('EmailOutboxStatus.Dead');
    }
  });

  test('SES reputation alarms (account-level metrics) only in prod', () => {
    expect(alarm(prod.observability, 'zladovce-prod-SesBounceRateHigh')).toMatchObject({
      Namespace: 'AWS/SES',
      MetricName: 'Reputation.BounceRate',
      Threshold: 0.05,
    });
    expect(alarm(prod.observability, 'zladovce-prod-SesComplaintRateHigh')).toMatchObject({
      Namespace: 'AWS/SES',
      MetricName: 'Reputation.ComplaintRate',
      Threshold: 0.001,
    });
    expect(alarm(prod.observability, 'zladovce-prod-SesBounceRateHigh').Dimensions).toBeUndefined();
    for (const envName of ['dev', 'staging'] as const) {
      const alarms = alarmsOf(synth(envName).observability);
      expect(alarms.some((a) => a.Properties.Namespace === 'AWS/SES')).toBe(false);
    }
    // Every alarm still notifies the topic.
    for (const a of alarmsOf(prod.observability)) {
      expect(a.Properties.AlarmActions).toHaveLength(1);
      expect(a.Properties.OKActions).toHaveLength(1);
    }
  });

  test('AWS Budget: account-wide, created by prod only, notifies the alarm topic', () => {
    dev.observability.resourceCountIs('AWS::Budgets::Budget', 0);
    synth('staging').observability.resourceCountIs('AWS::Budgets::Budget', 0);
    prod.observability.resourceCountIs('AWS::Budgets::Budget', 1);
    prod.observability.hasResourceProperties('AWS::Budgets::Budget', {
      Budget: {
        BudgetName: 'zladovce-account-monthly-cost',
        BudgetType: 'COST',
        TimeUnit: 'MONTHLY',
        BudgetLimit: { Amount: 120, Unit: 'USD' },
      },
    });
    const [budget] = Object.values(prod.observability.findResources('AWS::Budgets::Budget')) as any[];
    const notifications = budget.Properties.NotificationsWithSubscribers;
    expect(notifications.map((n: any) => `${n.Notification.NotificationType}:${n.Notification.Threshold}`)).toEqual([
      'ACTUAL:80',
      'ACTUAL:100',
      'FORECASTED:100',
    ]);
    for (const n of notifications) {
      expect(n.Subscribers).toEqual([{ SubscriptionType: 'SNS', Address: { Ref: expect.stringMatching(/^AlarmTopic/) } }]);
    }
    prod.observability.hasResourceProperties('AWS::SNS::TopicPolicy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({ Sid: 'AllowAwsBudgetsToPublish', Principal: { Service: 'budgets.amazonaws.com' } }),
        ]),
      },
    });
    // Configured addresses are added as e-mail subscribers.
    const withEmail = synth('prod', {
      budget: { ...getEnvironmentConfig('prod').budget!, notificationEmails: ['owner@example.com'] },
    });
    const [custom] = Object.values(withEmail.observability.findResources('AWS::Budgets::Budget')) as any[];
    expect(custom.Properties.NotificationsWithSubscribers[0].Subscribers).toContainEqual({
      SubscriptionType: 'EMAIL',
      Address: 'owner@example.com',
    });
  });
});

describe('configuration (Phase 6)', () => {
  const dev = getEnvironmentConfig('dev');
  const prod = getEnvironmentConfig('prod');

  test('budget only in prod; e-mail per environment as designed', () => {
    expect(getEnvironmentConfig('dev').budget).toBeUndefined();
    expect(getEnvironmentConfig('staging').budget).toBeUndefined();
    expect(prod.budget).toBeDefined();
    expect(dev.email).toMatchObject({ enabled: false, identity: { mode: 'none' } });
    expect(getEnvironmentConfig('staging').email.identity).toEqual({ mode: 'import', domain: 'zladovce.com' });
    expect(prod.email.identity.mode).toBe('create');
    expect(ENV_NAMES.filter((envName) => getEnvironmentConfig(envName).observability.sesReputationAlarms)).toEqual(['prod']);
  });

  test('invalid e-mail / storefront / worker settings are rejected', () => {
    const invalid: Array<[Parameters<typeof withOverrides>[1], RegExp]> = [
      [{ email: { ...dev.email, enabled: true, fromAddress: 'a@zladovce.com' } }, /email\.enabled requires an SES identity/],
      [{ email: { ...prod.email, fromAddress: undefined } }, /email\.fromAddress is required/],
      [{ email: { ...prod.email, fromAddress: 'porudzbine@example.com' } }, /must be an address of the identity domain/],
      [{ email: { ...prod.email, logoUrl: 'http://zladovce.com/logo.png' } }, /email\.logoUrl/],
      [{ email: { ...prod.email, identity: { mode: 'create', domain: 'zladovce.com', mailFromDomain: 'mail.example.com' } } }, /mailFromDomain/],
      [{ storefront: { baseUrl: 'http://zladovce.com' } }, /storefront\.baseUrl/],
      [{ storefront: { baseUrl: 'https://zladovce.com/' } }, /storefront\.baseUrl/],
      [{ outboxWorker: { timeoutSeconds: 30 } }, /outboxWorker\.timeoutSeconds/],
      [{ outboxWorker: { timeoutSeconds: 300 } }, /outboxWorker\.timeoutSeconds/],
      [{ outboxWorker: { batchSize: 0 } }, /outboxWorker\.batchSize/],
      [{ budget: { ...prod.budget!, monthlyLimitUsd: 0 } }, /monthlyLimitUsd/],
      [{ budget: { ...prod.budget!, notificationEmails: ['not-an-address'] } }, /notificationEmails/],
    ];
    for (const [overrides, message] of invalid) {
      expect(() => validateConfig(withOverrides(prod, overrides))).toThrow(message);
    }
    expect(() =>
      validateConfig(withOverrides(dev, { api: { routeThrottling: { ...dev.api.routeThrottling, createOrder: { rateLimit: 0, burstLimit: 1 } } } })),
    ).toThrow(/routeThrottling\.createOrder/);
    // E-mail may be disabled in an environment that has an identity (rows stay Pending).
    expect(() => validateConfig(withOverrides(prod, { email: { ...prod.email, enabled: false } }))).not.toThrow();
  });
});

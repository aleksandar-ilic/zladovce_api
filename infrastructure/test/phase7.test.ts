import { Template } from 'aws-cdk-lib/assertions';
import {
  COGNITO_ADMIN_ACTIONS,
  ENV_NAMES,
  getEnvironmentConfig,
  SITE_REBUILD_PLACEHOLDER_MARKER,
  UPLOADS_PREFIX,
  validateConfig,
  withOverrides,
} from '../lib/config';
import { invitationMessage, TEMPORARY_PASSWORD_VALIDITY_DAYS } from '../lib/stacks/auth-stack';
import { AMPLIFY_TEST_OVERRIDES, onlyResource, synth, SynthResult } from './helpers';

/* Phase 7 (analytics and CMS): administrator management, invitation e-mail, rebuild webhook, logo in e-mails. */

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
const title = (envName: string): string => envName.charAt(0).toUpperCase() + envName.slice(1);

/** Every IAM statement of every role and stand-alone policy of all stacks, labelled `<stack>/<logical id>`. */
function allStatements(result: SynthResult): Array<{ owner: string; statement: any }> {
  const templates: Record<string, Template> = {
    network: result.network,
    database: result.database,
    auth: result.auth,
    media: result.media,
    migrations: result.migrations,
    notifications: result.notifications,
    api: result.api,
    observability: result.observability,
  };
  const found: Array<{ owner: string; statement: any }> = [];
  for (const [name, template] of Object.entries(templates)) {
    for (const [id, role] of Object.entries(template.findResources('AWS::IAM::Role')) as Array<[string, any]>) {
      for (const policy of role.Properties.Policies ?? []) {
        for (const statement of policy.PolicyDocument.Statement) found.push({ owner: `${name}/${id}`, statement });
      }
    }
    for (const [id, policy] of Object.entries(template.findResources('AWS::IAM::Policy')) as Array<[string, any]>) {
      for (const statement of policy.Properties.PolicyDocument.Statement) found.push({ owner: `${name}/${id}`, statement });
    }
  }
  return found;
}

describe('Cognito administration (API role)', () => {
  test("exactly the six admin actions, on the environment's own user pool ARN only", () => {
    expect([...COGNITO_ADMIN_ACTIONS].sort()).toEqual([
      'cognito-idp:AdminAddUserToGroup',
      'cognito-idp:AdminCreateUser',
      'cognito-idp:AdminDisableUser',
      'cognito-idp:AdminEnableUser',
      'cognito-idp:AdminGetUser',
      'cognito-idp:AdminRemoveUserFromGroup',
    ]);
    for (const envName of ENV_NAMES) {
      const result = synth(envName);
      const statements = roleStatements(result.api, 'ApiRole').filter((s) =>
        actionsOf(s).some((action) => action.startsWith('cognito-idp:')),
      );
      expect(statements).toHaveLength(1);
      const [statement] = statements;
      expect(statement.Effect).toBe('Allow');
      expect([...actionsOf(statement)].sort()).toEqual([...COGNITO_ADMIN_ACTIONS].sort());
      // A single resource: the ARN of this environment's pool (cross-stack import from its own Auth stack).
      expect(statement.Resource).toEqual({
        'Fn::ImportValue': expect.stringMatching(new RegExp(`^Zladovce-${title(envName)}-Auth:.*AdminUserPool.*Arn`)),
      });
      expect(statement.Condition).toBeUndefined();
    }
  });

  test('no other role or policy in any stack has a Cognito permission; no Cognito wildcard anywhere', () => {
    for (const envName of ENV_NAMES) {
      for (const { owner, statement } of allStatements(synth(envName))) {
        const cognito = actionsOf(statement).filter((action) => action.startsWith('cognito-idp:'));
        if (cognito.length === 0) continue;
        expect(owner).toMatch(/^api\/ApiRole/);
        expect(cognito.some((action) => action.includes('*'))).toBe(false);
        expect(JSON.stringify(statement.Resource)).not.toContain('*');
      }
    }
  });

  test('the outbox worker has no Cognito permission', () => {
    const statements = roleStatements(synth('prod').api, 'OutboxWorkerRole');
    expect(statements.some((s) => actionsOf(s).some((action) => action.startsWith('cognito-idp:')))).toBe(false);
  });

  test('the API keeps the pool id, client id and region the backend reads (Auth:Cognito:*)', () => {
    const variables = functionByName(synth('staging').api, 'zladovce-staging-api').Properties.Environment.Variables;
    expect(variables.Auth__Cognito__Region).toBe('eu-central-1');
    expect(JSON.stringify(variables.Auth__Cognito__UserPoolId)).toContain('Zladovce-Staging-Auth');
    expect(JSON.stringify(variables.Auth__Cognito__ClientId)).toContain('Zladovce-Staging-Auth');
  });
});

describe('user pool for invitations', () => {
  test('e-mail is the user name (sign-in unchanged): username attribute, no aliases, case-insensitive', () => {
    for (const envName of ENV_NAMES) {
      const pool = onlyResource(synth(envName).auth, 'AWS::Cognito::UserPool').resource.Properties;
      expect(pool.UsernameAttributes).toEqual(['email']);
      expect(pool.AliasAttributes).toBeUndefined();
      expect(pool.UsernameConfiguration).toEqual({ CaseSensitive: false });
      expect(pool.AutoVerifiedAttributes).toEqual(['email']);
    }
  });

  test("Serbian invitation template with {username}, {####} and the environment's admin URL", () => {
    for (const [envName, adminUrl] of [
      ['dev', 'http://localhost:4201'],
      ['staging', 'https://admin-staging.zladovce.com'],
      ['prod', 'https://admin.zladovce.com'],
    ] as const) {
      const pool = onlyResource(synth(envName).auth, 'AWS::Cognito::UserPool').resource.Properties;
      const template = pool.AdminCreateUserConfig.InviteMessageTemplate;
      expect(pool.AdminCreateUserConfig.AllowAdminCreateUserOnly).toBe(true);
      expect(template.EmailMessage).toContain('{username}');
      expect(template.EmailMessage).toContain('{####}');
      expect(template.EmailMessage).toContain(`<a href="${adminUrl}">${adminUrl}</a>`);
      expect(template.EmailMessage).toContain('Privremena lozinka');
      expect(template.EmailMessage).toContain(`${TEMPORARY_PASSWORD_VALIDITY_DAYS} dana`);
      expect(template.EmailSubject).toMatch(/^Pozivnica za administraciju prodavnice Zladovce/);
      // Cognito limits: subject 1-140, message 6-20000 characters.
      expect(template.EmailSubject.length).toBeLessThanOrEqual(140);
      expect(template.EmailMessage.length).toBeLessThanOrEqual(20000);
      expect(pool.Policies.PasswordPolicy.TemporaryPasswordValidityDays).toBe(TEMPORARY_PASSWORD_VALIDITY_DAYS);
      // Still Cognito's own sender (no SES identity required at deploy time).
      expect(pool.EmailConfiguration?.EmailSendingAccount ?? 'COGNITO_DEFAULT').toBe('COGNITO_DEFAULT');
    }
    expect(invitationMessage(getEnvironmentConfig('prod')).subject).toBe('Pozivnica za administraciju prodavnice Zladovce');
    expect(invitationMessage(getEnvironmentConfig('staging')).subject).toContain('(staging)');
  });

  test('groups Owner and Staff still exist', () => {
    const auth = synth('prod').auth;
    auth.hasResourceProperties('AWS::Cognito::UserPoolGroup', { GroupName: 'Owner' });
    auth.hasResourceProperties('AWS::Cognito::UserPoolGroup', { GroupName: 'Staff' });
  });

  test('auth.adminUrl is validated (https, or local http; nothing that breaks the HTML e-mail)', () => {
    const staging = getEnvironmentConfig('staging');
    const withAdminUrl = (adminUrl: string) => withOverrides(staging, { auth: { ...staging.auth, adminUrl } });
    for (const valid of ['https://admin.zladovce.com', 'https://example.com/admin', 'http://localhost:4201', 'http://localhost']) {
      expect(() => validateConfig(withAdminUrl(valid))).not.toThrow();
    }
    for (const invalid of [
      'https://admin.zladovce.com/',
      'http://admin.zladovce.com',
      'https://admin.zladovce.com/?x=1',
      'https://admin.zladovce.com/"onmouseover="x',
      'https://admin.zladovce.com/<b>',
      'admin.zladovce.com',
    ]) {
      expect(() => validateConfig(withAdminUrl(invalid))).toThrow(/auth\.adminUrl/);
    }
  });
});

describe('storefront rebuild webhook secret', () => {
  test('per environment, placeholder the backend treats as "not configured", retained in prod', () => {
    for (const [envName, policy] of [
      ['dev', 'Delete'],
      ['staging', 'Delete'],
      ['prod', 'Retain'],
    ] as const) {
      const result = synth(envName);
      const secret = Object.values(result.notifications.findResources('AWS::SecretsManager::Secret')).find(
        (candidate: any) => candidate.Properties.Name === `zladovce/${envName}/site-rebuild-webhook-url`,
      ) as any;
      expect(secret).toBeDefined();
      expect(secret.DeletionPolicy).toBe(policy);
      // No URL in the template. The backend (SiteRebuildDispatcher.ParseSecret) reads a plain URL or {"url": ...};
      // a JSON document without "url" yields no URL, so nothing is POSTed.
      expect(secret.Properties.SecretString).toBeUndefined();
      const generate = secret.Properties.GenerateSecretString;
      expect(JSON.parse(generate.SecretStringTemplate)).toEqual({ status: SITE_REBUILD_PLACEHOLDER_MARKER });
      expect(generate.GenerateStringKey).not.toBe('url');
      result.notifications.hasOutput('SiteRebuildWebhookSecretName', {
        Value: `zladovce/${envName}/site-rebuild-webhook-url`,
      });
    }
  });

  test('only the outbox worker may read it', () => {
    for (const envName of ENV_NAMES) {
      const result = synth(envName);
      for (const { owner, statement } of allStatements(result)) {
        if (!JSON.stringify(statement.Resource ?? '').includes('SiteRebuildWebhookSecret')) continue;
        expect(owner).toMatch(/^api\/OutboxWorkerRole/);
        expect(statement.Action).toBe('secretsmanager:GetSecretValue');
        expect(statement.Resource).toEqual({
          'Fn::ImportValue': expect.stringMatching(
            new RegExp(`^Zladovce-${title(envName)}-Notifications:.*SiteRebuildWebhookSecret`),
          ),
        });
      }
      const worker = roleStatements(result.api, 'OutboxWorkerRole');
      expect(worker.filter((s) => JSON.stringify(s.Resource ?? '').includes('SiteRebuildWebhookSecret'))).toHaveLength(1);
    }
  });
});

describe('outbox worker environment (Phase 7)', () => {
  test('Site__RebuildWebhookUrlSecretArn and Media__BaseUrl (https CloudFront, no trailing slash)', () => {
    for (const envName of ENV_NAMES) {
      const result = synth(envName);
      const variables = functionByName(result.api, `zladovce-${envName}-outbox-worker`).Properties.Environment.Variables;
      expect(JSON.stringify(variables.Site__RebuildWebhookUrlSecretArn)).toMatch(
        new RegExp(`Zladovce-${title(envName)}-Notifications:.*SiteRebuildWebhookSecret`),
      );
      // Never the URL itself.
      expect(variables.Site__RebuildWebhookUrl).toBeUndefined();
      // Same value as the API's Media__BaseUrl: "https://" + the distribution domain, nothing after it.
      const apiVariables = functionByName(result.api, `zladovce-${envName}-api`).Properties.Environment.Variables;
      expect(variables.Media__BaseUrl).toEqual(apiVariables.Media__BaseUrl);
      const parts = variables.Media__BaseUrl['Fn::Join'][1];
      expect(parts).toHaveLength(2);
      expect(parts[0]).toBe('https://');
      expect(JSON.stringify(parts[1])).toContain('Distribution');
      // The API itself never calls the webhook.
      expect(apiVariables.Site__RebuildWebhookUrlSecretArn).toBeUndefined();
    }
  });

  test('with a custom media domain the worker gets that https URL', () => {
    const result = synth('staging', {
      domains: {
        media: { domainName: 'media-staging.zladovce.com', certificateArn: 'arn:aws:acm:us-east-1:111111111111:certificate/test' },
      },
    });
    const variables = functionByName(result.api, 'zladovce-staging-outbox-worker').Properties.Environment.Variables;
    expect(variables.Media__BaseUrl).toBe('https://media-staging.zladovce.com');
  });
});

describe('image uploads of the Phase 7 purposes', () => {
  const dev = synth('dev');
  const purposes = ['product', 'category', 'blog', 'page', 'logo'];

  test('the S3 notification prefix (uploads/) matches uploads/blog/, uploads/page/ and uploads/logo/', () => {
    const { resource } = onlyResource(dev.media, 'Custom::S3BucketNotifications');
    const configurations = resource.Properties.NotificationConfiguration.LambdaFunctionConfigurations;
    expect(configurations).toHaveLength(1);
    const rules = configurations[0].Filter.Key.FilterRules;
    // A prefix filter only: no suffix rule that could exclude a purpose or a file type.
    expect(rules).toEqual([{ Name: 'prefix', Value: UPLOADS_PREFIX }]);
    for (const purpose of purposes) {
      const key = `uploads/${purpose}/0f8fad5b-d9cb-469f-a165-70867728950e.jpg`;
      expect(key.startsWith(rules[0].Value)).toBe(true);
    }
  });

  test('presigning (API) and reading/deleting uploads (image processor) cover every purpose folder', () => {
    const presign = roleStatements(dev.api, 'ApiRole').find((s) => s.Sid === 'PresignUploads');
    expect(JSON.stringify(presign.Resource)).toContain(`/${UPLOADS_PREFIX}*`);
    const processor = roleStatements(dev.media, 'ImageProcessor').filter((s) =>
      JSON.stringify(s.Resource).includes(`/${UPLOADS_PREFIX}*`),
    );
    expect(processor.length).toBeGreaterThan(0);
  });
});

describe('Amplify build hook (when Amplify is enabled)', () => {
  test('the frontend stack prints the one-time create-webhook command for the storefront branch', () => {
    const result = synth('staging', AMPLIFY_TEST_OVERRIDES);
    const output = Object.values(result.frontend!.findOutputs('StorefrontRebuildWebhookCommand'))[0] as any;
    expect(output).toBeDefined();
    const text = JSON.stringify(output.Value);
    expect(text).toContain('aws amplify create-webhook --app-id ');
    expect(text).toContain('StorefrontApp');
    expect(text).toContain('StorefrontBranch');
    expect(output.Description).toContain('zladovce/staging/site-rebuild-webhook-url');
  });
});

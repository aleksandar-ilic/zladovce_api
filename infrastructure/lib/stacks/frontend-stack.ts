import * as fs from 'fs';
import * as path from 'path';
import { CfnOutput, SecretValue, Stack, StackProps } from 'aws-cdk-lib';
import * as amplify from 'aws-cdk-lib/aws-amplify';
import { Construct } from 'constructs';
import { INFRA_ROOT } from '../artifacts';
import { EnvironmentConfig, resourcePrefix, siteRebuildWebhookSecretName } from '../config';

export interface FrontendStackProps extends StackProps {
  readonly config: EnvironmentConfig;
  readonly apiUrl: string;
  readonly mediaBaseUrl: string;
  readonly mediaBucketName: string;
  readonly userPoolId: string;
  readonly userPoolClientId: string;
}

export interface HeaderEntry {
  readonly key: string;
  readonly value: string;
}

/** Renders the `customHeaders` YAML document Amplify Hosting expects. Values must not contain double quotes. */
export function renderCustomHeaders(headers: HeaderEntry[]): string {
  const lines = ['customHeaders:', "  - pattern: '**/*'", '    headers:'];
  for (const header of headers) {
    lines.push(`      - key: "${header.key}"`);
    lines.push(`        value: "${header.value}"`);
  }
  return lines.join('\n') + '\n';
}

/**
 * Rewrites every request that is not a static file to index.html (client-side routing).
 * Standard Amplify single-page-application rule.
 */
export const SPA_REWRITE_SOURCE =
  '</^[^.]+$|\\.(?!(css|gif|ico|jpg|jpeg|js|png|txt|svg|woff|woff2|ttf|map|json|webp|avif|webmanifest|xml)$)([^.]+$)/>';

function readBuildSpec(fileName: string): string {
  return fs.readFileSync(path.join(INFRA_ROOT, 'amplify', fileName), 'utf8');
}

/**
 * Amplify Hosting apps for the storefront and the admin application, both built
 * from the root of the frontend repository (zladovce_web, `amplify.repository`).
 *
 * Only instantiated when `amplify.enabled` is true, because connecting a
 * repository needs a Git provider access token stored in Secrets Manager.
 * Custom domains for the two apps are NOT configured here yet (DNS is an open
 * question) - associate them in the Amplify console or add `CfnDomain` later.
 *
 * The frontend build receives only public values as environment variables.
 */
export class FrontendStack extends Stack {
  public readonly storefrontApp: amplify.CfnApp;
  public readonly adminApp: amplify.CfnApp;

  constructor(scope: Construct, id: string, props: FrontendStackProps) {
    super(scope, id, props);
    const { config } = props;
    const cfg = config.amplify;
    if (!cfg.repository || !cfg.accessTokenSecretName) {
      throw new Error('amplify.repository and amplify.accessTokenSecretName are required for the frontend stack.');
    }

    // Resolved by CloudFormation at deploy time ({{resolve:secretsmanager:...}}); never in the template.
    const accessToken = SecretValue.secretsManager(cfg.accessTokenSecretName).unsafeUnwrap();
    const prefix = resourcePrefix(config);
    const cspHeaderName = cfg.cspReportOnly ? 'Content-Security-Policy-Report-Only' : 'Content-Security-Policy';

    const baseHeaders: HeaderEntry[] = [
      { key: 'Strict-Transport-Security', value: 'max-age=31536000; includeSubDomains' },
      { key: 'X-Content-Type-Options', value: 'nosniff' },
      { key: 'Referrer-Policy', value: 'strict-origin-when-cross-origin' },
      { key: 'X-Frame-Options', value: 'DENY' },
      { key: 'Permissions-Policy', value: 'camera=(), microphone=(), geolocation=()' },
    ];

    const csp = (connectSources: string[]): string =>
      [
        "default-src 'self'",
        "script-src 'self'",
        // Angular component styles are injected as <style> elements.
        "style-src 'self' 'unsafe-inline'",
        `img-src 'self' data: blob: ${props.mediaBaseUrl}`,
        "font-src 'self' data:",
        `connect-src 'self' ${connectSources.join(' ')}`,
        "object-src 'none'",
        "base-uri 'self'",
        "form-action 'self'",
        "frame-ancestors 'none'",
      ].join('; ');

    // The frontend repository has the Angular workspace at its root: no AMPLIFY_MONOREPO_APP_ROOT.
    const publicEnvironment = [
      { name: 'ZLADOVCE_ENVIRONMENT', value: config.envName },
      { name: 'ZLADOVCE_API_URL', value: props.apiUrl },
      { name: 'ZLADOVCE_MEDIA_BASE_URL', value: props.mediaBaseUrl },
    ];

    const branchStage = config.isProduction ? 'PRODUCTION' : config.envName === 'staging' ? 'BETA' : 'DEVELOPMENT';

    /* -------------------------------- Storefront -------------------------------- */

    this.storefrontApp = new amplify.CfnApp(this, 'StorefrontApp', {
      name: `${prefix}-storefront`,
      description: `Zladovce ${config.envName} storefront (prerendered)`,
      platform: 'WEB',
      repository: cfg.repository,
      accessToken,
      buildSpec: readBuildSpec('storefront.amplify.yml'),
      environmentVariables: publicEnvironment,
      customHeaders: renderCustomHeaders([
        ...baseHeaders,
        { key: cspHeaderName, value: csp([props.apiUrl]) },
      ]),
      // No SPA rewrite: the storefront is prerendered. Whether a fallback is needed is
      // decided by the Phase 2 rendering spike.
    });
    const storefrontBranch = new amplify.CfnBranch(this, 'StorefrontBranch', {
      appId: this.storefrontApp.attrAppId,
      branchName: cfg.branch,
      enableAutoBuild: true,
      framework: 'Angular',
      stage: branchStage,
    });

    /* ----------------------------------- Admin ---------------------------------- */

    this.adminApp = new amplify.CfnApp(this, 'AdminApp', {
      name: `${prefix}-admin`,
      description: `Zladovce ${config.envName} admin (single-page application)`,
      platform: 'WEB',
      repository: cfg.repository,
      accessToken,
      buildSpec: readBuildSpec('admin.amplify.yml'),
      environmentVariables: [
        ...publicEnvironment,
        { name: 'ZLADOVCE_COGNITO_REGION', value: this.region },
        { name: 'ZLADOVCE_COGNITO_USER_POOL_ID', value: props.userPoolId },
        { name: 'ZLADOVCE_COGNITO_CLIENT_ID', value: props.userPoolClientId },
      ],
      customRules: [{ source: SPA_REWRITE_SOURCE, target: '/index.html', status: '200' }],
      customHeaders: renderCustomHeaders([
        ...baseHeaders,
        { key: 'X-Robots-Tag', value: 'noindex, nofollow' },
        {
          key: cspHeaderName,
          value: csp([
            props.apiUrl,
            `https://cognito-idp.${this.region}.amazonaws.com`,
            // Direct browser uploads with pre-signed POST.
            `https://${props.mediaBucketName}.s3.${this.region}.amazonaws.com`,
          ]),
        },
      ]),
    });
    const adminBranch = new amplify.CfnBranch(this, 'AdminBranch', {
      appId: this.adminApp.attrAppId,
      branchName: cfg.branch,
      enableAutoBuild: true,
      framework: 'Angular',
      stage: branchStage,
    });

    new CfnOutput(this, 'StorefrontAppId', { value: this.storefrontApp.attrAppId });
    new CfnOutput(this, 'StorefrontUrl', {
      value: `https://${storefrontBranch.attrBranchName}.${this.storefrontApp.attrDefaultDomain}`,
      description: 'Add this origin to cors.storefrontOrigins if no custom domain is used',
    });
    // CloudFormation has no Amplify webhook resource: the build hook the outbox worker calls after content changes is
    // created once by hand and stored in the rebuild-webhook secret (Notifications stack, README "Storefront rebuild").
    new CfnOutput(this, 'StorefrontRebuildWebhookCommand', {
      value:
        `aws amplify create-webhook --app-id ${this.storefrontApp.attrAppId} --branch-name ${storefrontBranch.attrBranchName} ` +
        `--description zladovce-${config.envName}-content-rebuild --query webhook.webhookUrl --output text`,
      description: `Create the storefront build hook once, then store its URL in ${siteRebuildWebhookSecretName(config)} (README)`,
    });
    new CfnOutput(this, 'AdminAppId', { value: this.adminApp.attrAppId });
    new CfnOutput(this, 'AdminUrl', {
      value: `https://${adminBranch.attrBranchName}.${this.adminApp.attrDefaultDomain}`,
      description: 'Add this origin to cors.adminOrigins if no custom domain is used',
    });
  }
}

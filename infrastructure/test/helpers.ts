import * as fs from 'fs';
import * as path from 'path';
import { App } from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { ConfigOverrides, EnvName, getEnvironmentConfig, withOverrides } from '../lib/config';
import { createZladovceEnvironment, ZladovceEnvironment } from '../lib/zladovce-environment';

/** Feature flags from cdk.json, so tests synthesise exactly what the CLI does. */
export const CDK_CONTEXT: Record<string, unknown> = JSON.parse(
  fs.readFileSync(path.join(__dirname, '..', 'cdk.json'), 'utf8'),
).context;

export interface SynthResult {
  readonly app: App;
  readonly environment: ZladovceEnvironment;
  readonly network: Template;
  readonly database: Template;
  readonly auth: Template;
  readonly media: Template;
  readonly migrations: Template;
  readonly api: Template;
  readonly observability: Template;
  readonly frontend?: Template;
}

export function buildApp(
  envName: EnvName,
  overrides: ConfigOverrides = {},
  options: { requireArtifacts?: boolean } = {},
): { app: App; environment: ZladovceEnvironment } {
  const app = new App({ context: CDK_CONTEXT });
  const config = withOverrides(getEnvironmentConfig(envName), overrides);
  const environment = createZladovceEnvironment(app, config, options);
  return { app, environment };
}

const cache = new Map<string, SynthResult>();

/** Synthesises one environment (cached per environment + overrides). */
export function synth(envName: EnvName, overrides: ConfigOverrides = {}): SynthResult {
  const key = `${envName}:${JSON.stringify(overrides)}`;
  const cached = cache.get(key);
  if (cached) return cached;

  const { app, environment } = buildApp(envName, overrides);
  const result: SynthResult = {
    app,
    environment,
    network: Template.fromStack(environment.network),
    database: Template.fromStack(environment.database),
    auth: Template.fromStack(environment.auth),
    media: Template.fromStack(environment.media),
    migrations: Template.fromStack(environment.migrations),
    api: Template.fromStack(environment.api),
    observability: Template.fromStack(environment.observability),
    frontend: environment.frontend ? Template.fromStack(environment.frontend) : undefined,
  };
  cache.set(key, result);
  return result;
}

/** Test-only Amplify settings. The repository URL is deliberately not a real one. */
export const AMPLIFY_TEST_OVERRIDES: ConfigOverrides = {
  amplify: {
    enabled: true,
    repository: 'https://git.example.invalid/owner/repository',
    accessTokenSecretName: 'zladovce/test/git-token',
  },
};

/** The single resource of a type; fails when there is not exactly one. */
export function onlyResource(template: Template, type: string): { logicalId: string; resource: any } {
  const found = template.findResources(type);
  const ids = Object.keys(found);
  if (ids.length !== 1) {
    throw new Error(`Expected exactly one ${type}, found ${ids.length}.`);
  }
  return { logicalId: ids[0], resource: found[ids[0]] };
}

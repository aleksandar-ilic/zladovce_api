#!/usr/bin/env node
import { App, Validations } from 'aws-cdk-lib';
import { AwsSolutionsChecks } from 'cdk-nag';
import { applyContextOverrides, ENV_NAMES, getEnvironmentConfig, isEnvName } from '../lib/config';
import { createZladovceEnvironment } from '../lib/zladovce-environment';

/**
 * Usage:
 *   npx cdk synth -c env=dev|staging|prod
 *   npx cdk deploy --all -c env=staging -c requireArtifacts=true
 *
 * Optional context: account, region, natMode, useRdsProxy, apiAssetPath,
 * migrationAssetPath, alarmEmail, requireArtifacts, nag.
 */
const app = new App();

const envName = app.node.tryGetContext('env') ?? 'dev';
if (!isEnvName(envName)) {
  throw new Error(`Unknown environment '${envName}'. Use -c env=${ENV_NAMES.join('|')}.`);
}

const config = applyContextOverrides(getEnvironmentConfig(envName), app.node);
const requireArtifacts = String(app.node.tryGetContext('requireArtifacts') ?? 'false') === 'true';

createZladovceEnvironment(app, config, { requireArtifacts });

// `-c nag=true` runs the cdk-nag AwsSolutions pack during synth (the unit tests always run it).
if (String(app.node.tryGetContext('nag') ?? 'false') === 'true') {
  Validations.of(app).addPlugins(new AwsSolutionsChecks(app, { verbose: true }));
}

app.synth();

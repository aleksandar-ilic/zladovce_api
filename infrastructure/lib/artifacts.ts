import * as fs from 'fs';
import * as path from 'path';
import { Annotations } from 'aws-cdk-lib';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import { Construct } from 'constructs';

/** Absolute path of the `infrastructure/` directory. */
export const INFRA_ROOT = path.resolve(__dirname, '..');
export const PLACEHOLDER_ASSET_DIR = path.join(INFRA_ROOT, 'assets', 'placeholder');

export interface LambdaCodeResolution {
  readonly code: lambda.Code;
  readonly isPlaceholder: boolean;
  readonly resolvedPath: string;
}

const warned = new Set<string>();

function artifactExists(absolutePath: string): boolean {
  if (!fs.existsSync(absolutePath)) return false;
  const stat = fs.statSync(absolutePath);
  if (stat.isFile()) return absolutePath.toLowerCase().endsWith('.zip');
  return stat.isDirectory() && fs.readdirSync(absolutePath).length > 0;
}

/**
 * Resolves the code asset of a .NET Lambda function.
 *
 * - The path (a publish directory or a .zip) is relative to `infrastructure/` unless absolute.
 * - If it does not exist and `requireArtifacts` is true (CI/deploy), synthesis FAILS.
 * - If it does not exist otherwise, a placeholder asset is used so that offline
 *   `cdk synth` and unit tests work, and a loud warning is printed. A function
 *   deployed with the placeholder cannot start.
 */
export function resolveLambdaCode(
  scope: Construct,
  label: string,
  assetPath: string,
  requireArtifacts: boolean,
): LambdaCodeResolution {
  const resolvedPath = path.isAbsolute(assetPath) ? assetPath : path.resolve(INFRA_ROOT, assetPath);

  if (artifactExists(resolvedPath)) {
    return { code: lambda.Code.fromAsset(resolvedPath), isPlaceholder: false, resolvedPath };
  }

  if (requireArtifacts) {
    throw new Error(
      `Lambda artifact for '${label}' not found at '${resolvedPath}' and requireArtifacts=true. ` +
        'Publish the backend first (see infrastructure/README.md) or pass the correct path with -c.',
    );
  }

  const message =
    `PLACEHOLDER LAMBDA CODE for '${label}': artifact not found at '${resolvedPath}'. ` +
    'This template is NOT deployable. Deploy with -c requireArtifacts=true.';
  Annotations.of(scope).addWarningV2(`zladovce:placeholder-artifact:${label}`, message);

  // Annotations are printed by the CDK CLI; the banner makes it impossible to miss. Silent under Jest.
  if (!process.env.JEST_WORKER_ID && !warned.has(label)) {
    warned.add(label);
    const line = '!'.repeat(100);
    console.warn(`\n${line}\n!! WARNING: ${message}\n${line}\n`);
  }

  return { code: lambda.Code.fromAsset(PLACEHOLDER_ASSET_DIR), isPlaceholder: true, resolvedPath };
}

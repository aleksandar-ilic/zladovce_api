import { CfnOutput, Duration, Stack, StackProps } from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as iam from 'aws-cdk-lib/aws-iam';
import { Construct } from 'constructs';
import { EnvironmentConfig, resourcePrefix } from '../config';
import { DotnetFunction } from '../constructs/dotnet-function';
import { DatabaseStack } from './database-stack';

export interface MigrationStackProps extends StackProps {
  readonly config: EnvironmentConfig;
  /** `-c requireArtifacts=true`: fail when the backend artifact is missing. */
  readonly requireArtifacts: boolean;
  readonly vpc: ec2.IVpc;
  readonly lambdaSecurityGroup: ec2.ISecurityGroup;
  readonly database: DatabaseStack;
}

/**
 * Migration runner Lambda. Invoked by the deployment pipeline only; it is not
 * connected to API Gateway.
 *
 * It is a separate stack on purpose: the pipeline deploys this stack, runs the
 * migrations, and only then deploys the API stack with the new application
 * version (docs/AWS_INFRASTRUCTURE.md W7).
 */
export class MigrationStack extends Stack {
  public readonly migrationFunction: DotnetFunction;

  constructor(scope: Construct, id: string, props: MigrationStackProps) {
    super(scope, id, props);
    const { config, database } = props;
    const db = config.database;

    // IAM authentication as the migration user, on the instance itself.
    const statements: iam.PolicyStatement[] = [database.instanceConnectStatement(db.migrationUser)];
    const environment: Record<string, string> = {
      ASPNETCORE_ENVIRONMENT: config.api.aspnetEnvironment,
      DOTNET_ENVIRONMENT: config.api.aspnetEnvironment,
      // Always the instance endpoint: schema changes do not go through RDS Proxy.
      Database__Host: database.instanceEndpointAddress,
      Database__Port: database.port,
      Database__Name: db.databaseName,
      Database__User: db.migrationUser,
      Database__UseIamAuth: 'true',
      Database__Region: this.region,
      Database__AppUser: db.appUser,
    };

    if (db.migrationRunnerCanReadMasterSecret) {
      // First-time bootstrap only: create the two IAM-authenticated roles on a new database.
      environment.Database__MasterSecretArn = database.masterSecret.secretArn;
      statements.push(
        new iam.PolicyStatement({
          sid: 'ReadMasterSecretForBootstrap',
          actions: ['secretsmanager:GetSecretValue'],
          resources: [database.masterSecret.secretArn],
        }),
      );
    }

    this.migrationFunction = new DotnetFunction(this, 'Migrations', {
      functionName: `${resourcePrefix(config)}-migrations`,
      description: `Zladovce ${config.envName} database migration runner (invoked by the pipeline only)`,
      handler: config.api.migrationHandler,
      assetPath: config.api.migrationAssetPath,
      requireArtifacts: props.requireArtifacts,
      memoryMb: 1024,
      timeout: Duration.seconds(config.api.migrationTimeoutSeconds),
      // Never run two migrations at once. Skipped when reservations are disabled for the account.
      reservedConcurrency: config.api.reservedConcurrency === undefined ? undefined : 1,
      logRetentionDays: config.api.logRetentionDays,
      retainLogs: config.isProduction,
      vpc: props.vpc,
      securityGroup: props.lambdaSecurityGroup,
      environment,
      policyStatements: statements,
    });

    new CfnOutput(this, 'MigrationFunctionName', {
      value: this.migrationFunction.function.functionName,
      description: 'Invoke before deploying the API stack to apply database migrations',
    });
  }
}

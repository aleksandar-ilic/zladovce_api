import { ArnFormat, CfnOutput, Duration, Fn, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';
import { EnvironmentConfig, POSTGRES_ENGINE_VERSION, resourcePrefix } from '../config';

export interface DatabaseStackProps extends StackProps {
  readonly config: EnvironmentConfig;
  readonly vpc: ec2.IVpc;
  readonly databaseSecurityGroup: ec2.ISecurityGroup;
  /** Required when `config.database.useRdsProxy` is true. */
  readonly proxySecurityGroup?: ec2.ISecurityGroup;
}

/**
 * RDS PostgreSQL: private, encrypted, single-AZ, IAM database authentication.
 * The master password is generated, stored and rotated by RDS in Secrets Manager;
 * the application never uses it.
 */
export class DatabaseStack extends Stack {
  public readonly instance: rds.DatabaseInstance;
  public readonly proxy?: rds.DatabaseProxy;
  /** RDS-managed master secret (used only for first-time role bootstrap). */
  public readonly masterSecret: secretsmanager.ISecret;
  /** Endpoint of the instance itself (always used by the migration runner). */
  public readonly instanceEndpointAddress: string;
  public readonly port: string;
  /** Endpoint the API connects to: the proxy when enabled, otherwise the instance. */
  public readonly appEndpointAddress: string;

  constructor(scope: Construct, id: string, props: DatabaseStackProps) {
    super(scope, id, props);
    const { config, vpc } = props;
    const db = config.database;
    const instanceIdentifier = resourcePrefix(config);

    // All rules between security groups are defined in NetworkStack. Importing the groups as immutable
    // stops RDS constructs from adding rules of their own there (which would create a stack cycle).
    const databaseSecurityGroup = ec2.SecurityGroup.fromSecurityGroupId(
      this,
      'DatabaseSecurityGroup',
      props.databaseSecurityGroup.securityGroupId,
      { mutable: false },
    );

    // Created up front so that it has a retention period (RDS would otherwise create it with "never expire").
    const postgresLogGroup = new logs.LogGroup(this, 'PostgresLogGroup', {
      logGroupName: `/aws/rds/instance/${instanceIdentifier}/postgresql`,
      retention: config.api.logRetentionDays as logs.RetentionDays,
      removalPolicy: config.isProduction ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });

    this.instance = new rds.DatabaseInstance(this, 'Postgres', {
      instanceIdentifier,
      engine: rds.DatabaseInstanceEngine.postgres({ version: POSTGRES_ENGINE_VERSION }),
      instanceType: new ec2.InstanceType(db.instanceType),
      vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [databaseSecurityGroup],
      publiclyAccessible: false,
      multiAz: false,
      allocatedStorage: db.allocatedStorageGb,
      storageType: rds.StorageType.GP3,
      storageEncrypted: true,
      databaseName: db.databaseName,
      credentials: rds.Credentials.fromUsername(db.masterUsername),
      manageMasterUserPassword: true,
      iamAuthentication: true,
      backupRetention: Duration.days(db.backupRetentionDays),
      deleteAutomatedBackups: db.removalPolicy === 'destroy',
      deletionProtection: db.deletionProtection,
      removalPolicy: db.removalPolicy === 'snapshot' ? RemovalPolicy.SNAPSHOT : RemovalPolicy.DESTROY,
      autoMinorVersionUpgrade: true,
      allowMajorVersionUpgrade: false,
      cloudwatchLogsExports: ['postgresql'],
      copyTagsToSnapshot: true,
    });
    this.instance.node.addDependency(postgresLogGroup);

    if (!this.instance.secret) {
      throw new Error('Expected an RDS-managed master secret.');
    }
    this.masterSecret = this.instance.secret;
    this.instanceEndpointAddress = this.instance.dbInstanceEndpointAddress;
    this.port = this.instance.dbInstanceEndpointPort;

    if (db.useRdsProxy) {
      if (!props.proxySecurityGroup) {
        throw new Error('proxySecurityGroup is required when useRdsProxy is true.');
      }
      // End-to-end IAM authentication: clients authenticate to the proxy with IAM and the
      // proxy authenticates to PostgreSQL with IAM as the same user. No passwords, no secrets.
      // With end-to-end IAM authentication the proxy's own role must be allowed to connect
      // to the instance as each database user it forwards. Only the application user goes
      // through the proxy; the migration runner always connects to the instance directly.
      const proxyRole = new iam.Role(this, 'ProxyRole', {
        assumedBy: new iam.ServicePrincipal('rds.amazonaws.com'),
        description: 'Zladovce RDS Proxy - connects to PostgreSQL with IAM authentication',
        inlinePolicies: {
          ConnectToDatabase: new iam.PolicyDocument({
            statements: [this.instanceConnectStatement(db.appUser)],
          }),
        },
      });
      this.proxy = new rds.DatabaseProxy(this, 'Proxy', {
        dbProxyName: `${instanceIdentifier}-proxy`,
        role: proxyRole,
        proxyTarget: rds.ProxyTarget.fromInstance(this.instance),
        vpc,
        vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
        securityGroups: [
          ec2.SecurityGroup.fromSecurityGroupId(this, 'ProxySecurityGroup', props.proxySecurityGroup.securityGroupId, {
            mutable: false,
          }),
        ],
        defaultAuthScheme: rds.DefaultAuthScheme.IAM_AUTH,
        iamAuth: true,
        requireTLS: true,
        // Kept below the ~80 connections a db.t4g.micro allows, leaving room for the migration runner.
        maxConnectionsPercent: 75,
      });
      this.appEndpointAddress = this.proxy.endpoint;
    } else {
      this.appEndpointAddress = this.instance.dbInstanceEndpointAddress;
    }

    new CfnOutput(this, 'DatabaseEndpoint', {
      value: this.instanceEndpointAddress,
      description: 'RDS PostgreSQL endpoint (private)',
    });
    new CfnOutput(this, 'DatabasePort', { value: this.port });
    new CfnOutput(this, 'DatabaseMasterSecretArn', {
      value: this.masterSecret.secretArn,
      description: 'RDS-managed master credentials (Secrets Manager)',
    });
    if (this.proxy) {
      new CfnOutput(this, 'DatabaseProxyEndpoint', { value: this.proxy.endpoint });
    }
  }

  /** `rds-db:connect` on the instance for exactly one database user. */
  public instanceConnectStatement(dbUser: string): iam.PolicyStatement {
    if (!this.instance.instanceResourceId) {
      throw new Error('Database instance has no resource id.');
    }
    return new iam.PolicyStatement({
      sid: 'RdsIamConnectInstance',
      actions: ['rds-db:connect'],
      resources: [this.dbUserArn(this.instance.instanceResourceId, dbUser)],
    });
  }

  /**
   * `rds-db:connect` for the API's database user on the endpoint the API uses
   * (the proxy when enabled, otherwise the instance).
   */
  public appConnectStatement(dbUser: string): iam.PolicyStatement {
    if (!this.proxy) return this.instanceConnectStatement(dbUser);
    // arn:aws:rds:region:account:db-proxy:prx-0123 -> prx-0123 (the id rds-db ARNs use for proxies)
    const proxyResourceId = Fn.select(6, Fn.split(':', this.proxy.dbProxyArn));
    return new iam.PolicyStatement({
      sid: 'RdsIamConnectProxy',
      actions: ['rds-db:connect'],
      resources: [this.dbUserArn(proxyResourceId, dbUser)],
    });
  }

  private dbUserArn(resourceId: string, dbUser: string): string {
    return this.formatArn({
      service: 'rds-db',
      resource: 'dbuser',
      resourceName: `${resourceId}/${dbUser}`,
      arnFormat: ArnFormat.COLON_RESOURCE_NAME,
    });
  }
}

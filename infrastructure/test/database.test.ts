import { Match } from 'aws-cdk-lib/assertions';
import { onlyResource, synth } from './helpers';

describe('database', () => {
  const dev = synth('dev');
  const staging = synth('staging');
  const prod = synth('prod');

  test('private, encrypted, IAM authentication, small single-AZ PostgreSQL 17 on gp3', () => {
    for (const { database } of [dev, staging, prod]) {
      database.resourceCountIs('AWS::RDS::DBInstance', 1);
      database.hasResourceProperties('AWS::RDS::DBInstance', {
        Engine: 'postgres',
        EngineVersion: Match.stringLikeRegexp('^17'),
        DBInstanceClass: 'db.t4g.micro',
        AllocatedStorage: '20',
        StorageType: 'gp3',
        StorageEncrypted: true,
        PubliclyAccessible: false,
        MultiAZ: false,
        EnableIAMDatabaseAuthentication: true,
        DBName: 'zladovce',
      });
    }
  });

  test('master password is managed by RDS in Secrets Manager and never in the template', () => {
    const { resource } = onlyResource(dev.database, 'AWS::RDS::DBInstance');
    expect(resource.Properties.ManageMasterUserPassword).toBe(true);
    expect(resource.Properties.MasterUserPassword).toBeUndefined();
    expect(resource.Properties.MasterUsername).toBe('zladovce_admin');
  });

  test('database lives in the private subnets', () => {
    const { resource } = onlyResource(dev.database, 'AWS::RDS::DBSubnetGroup');
    const subnetRefs = JSON.stringify(resource.Properties.SubnetIds);
    expect(subnetRefs).toContain('PrivateSubnet1');
    expect(subnetRefs).toContain('PrivateSubnet2');
    expect(subnetRefs).not.toContain('PublicSubnet');
  });

  test('dev: 1-day backups, no deletion protection, destroyed with the stack', () => {
    const { resource } = onlyResource(dev.database, 'AWS::RDS::DBInstance');
    expect(resource.Properties.BackupRetentionPeriod).toBe(1);
    expect(resource.Properties.DeletionProtection).toBe(false);
    expect(resource.DeletionPolicy).toBe('Delete');
  });

  test('staging: 1-day backups, snapshot on removal', () => {
    const { resource } = onlyResource(staging.database, 'AWS::RDS::DBInstance');
    expect(resource.Properties.BackupRetentionPeriod).toBe(1);
    expect(resource.Properties.DeletionProtection).toBe(false);
    expect(resource.DeletionPolicy).toBe('Snapshot');
  });

  test('prod: 7-day backups, deletion protection, snapshot on removal', () => {
    const { resource } = onlyResource(prod.database, 'AWS::RDS::DBInstance');
    expect(resource.Properties.BackupRetentionPeriod).toBe(7);
    expect(resource.Properties.DeletionProtection).toBe(true);
    expect(resource.DeletionPolicy).toBe('Snapshot');
    expect(resource.UpdateReplacePolicy).toBe('Snapshot');
  });

  test('no RDS Proxy by default; the API connects to the instance', () => {
    dev.database.resourceCountIs('AWS::RDS::DBProxy', 0);
    const apiFunction = dev.api.findResources('AWS::Lambda::Function', {
      Properties: { FunctionName: 'zladovce-dev-api' },
    });
    const host = JSON.stringify(Object.values(apiFunction)[0].Properties.Environment.Variables.Database__Host);
    expect(host).toContain('Postgres');
    expect(host).not.toContain('Proxy');
  });
});

describe('database (useRdsProxy=true)', () => {
  const { database, network, api, migrations } = synth('dev', { database: { useRdsProxy: true } });

  test('adds an RDS Proxy with TLS and end-to-end IAM authentication', () => {
    database.resourceCountIs('AWS::RDS::DBProxy', 1);
    database.resourceCountIs('AWS::RDS::DBProxyTargetGroup', 1);
    database.hasResourceProperties('AWS::RDS::DBProxy', {
      EngineFamily: 'POSTGRESQL',
      RequireTLS: true,
      DefaultAuthScheme: 'IAM_AUTH',
    });
  });

  test('proxy role may connect to the instance as the application user only', () => {
    const roles = Object.values(database.findResources('AWS::IAM::Role')) as any[];
    const statements = roles.flatMap((role) =>
      (role.Properties.Policies ?? []).flatMap((policy: any) => policy.PolicyDocument.Statement),
    );
    const connect = statements.filter((statement: any) => statement.Action === 'rds-db:connect');
    expect(connect).toHaveLength(1);
    expect(JSON.stringify(connect[0].Resource)).toContain('/zladovce_app');
  });

  test('network gets a proxy security group between Lambda and the database', () => {
    const groupIds = Object.keys(network.findResources('AWS::EC2::SecurityGroup'));
    expect(groupIds.some((id) => id.startsWith('ProxySecurityGroup'))).toBe(true);
    // Lambda -> database, Lambda -> proxy, proxy -> database
    network.resourceCountIs('AWS::EC2::SecurityGroupIngress', 3);
  });

  test('API connects to the proxy; the migration runner still connects to the instance', () => {
    const functions = { ...api.findResources('AWS::Lambda::Function'), ...migrations.findResources('AWS::Lambda::Function') };
    const byName = (name: string): any =>
      Object.values(functions).find((fn: any) => fn.Properties.FunctionName === name);
    const apiHost = JSON.stringify(byName('zladovce-dev-api').Properties.Environment.Variables.Database__Host);
    const migrationHost = JSON.stringify(
      byName('zladovce-dev-migrations').Properties.Environment.Variables.Database__Host,
    );
    expect(apiHost).toContain('Proxy');
    expect(migrationHost).not.toContain('Proxy');
    expect(migrationHost).toContain('Postgres');
  });
});

import { Stack, StackProps } from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';
import { EnvironmentConfig } from '../config';

export interface NetworkStackProps extends StackProps {
  readonly config: EnvironmentConfig;
}

/**
 * VPC (2 AZs, public + private subnets), outbound NAT, S3 gateway endpoint and
 * all security groups. Security groups live here (not next to the resources
 * that use them) so that rules between them never create cross-stack cycles.
 */
export class NetworkStack extends Stack {
  public readonly vpc: ec2.Vpc;
  /** Security group of every VPC-attached Lambda function. */
  public readonly lambdaSecurityGroup: ec2.SecurityGroup;
  public readonly databaseSecurityGroup: ec2.SecurityGroup;
  /** Only created when `database.useRdsProxy` is true. */
  public readonly proxySecurityGroup?: ec2.SecurityGroup;
  /** Instance ids of the NAT instances (empty in gateway mode). */
  public readonly natInstanceIds: string[] = [];

  constructor(scope: Construct, id: string, props: NetworkStackProps) {
    super(scope, id, props);
    const { config } = props;

    const natInstanceProvider =
      config.network.natMode === 'instance'
        ? ec2.NatProvider.instanceV2({
            instanceType: new ec2.InstanceType(config.network.natInstanceType),
            // Amazon Linux 2023, resolved by CloudFormation from a public SSM
            // parameter at deploy time - no lookup at synth time.
            associatePublicIpAddress: true,
            // Inbound is opened below for the VPC only (the default would be 0.0.0.0/0).
            defaultAllowedTraffic: ec2.NatTrafficDirection.OUTBOUND_ONLY,
          })
        : undefined;

    this.vpc = new ec2.Vpc(this, 'Vpc', {
      ipAddresses: ec2.IpAddresses.cidr(config.network.cidr),
      availabilityZones: config.network.availabilityZones,
      natGateways: 1,
      natGatewayProvider: natInstanceProvider ?? ec2.NatProvider.gateway(),
      subnetConfiguration: [
        { name: 'Public', subnetType: ec2.SubnetType.PUBLIC, cidrMask: 24, mapPublicIpOnLaunch: false },
        { name: 'Private', subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS, cidrMask: 24 },
      ],
      // S3 traffic (media bucket) stays on the AWS network and is free of NAT charges.
      gatewayEndpoints: {
        S3: { service: ec2.GatewayVpcEndpointAwsService.S3 },
      },
      // `true` would add a CDK custom-resource Lambda; nothing uses the default security group.
      restrictDefaultSecurityGroup: false,
    });

    if (natInstanceProvider) {
      // Lambdas only need HTTPS outbound (SES, Cognito, Secrets Manager, STS).
      natInstanceProvider.securityGroup.addIngressRule(
        ec2.Peer.ipv4(this.vpc.vpcCidrBlock),
        ec2.Port.tcp(443),
        'HTTPS from the VPC to be forwarded to the internet',
      );
      for (const instance of natInstanceProvider.gatewayInstances) {
        this.natInstanceIds.push(instance.instanceId);
      }
    }

    if (config.network.flowLogs) {
      const flowLogGroup = new logs.LogGroup(this, 'FlowLogGroup', {
        retention: logs.RetentionDays.ONE_MONTH,
      });
      this.vpc.addFlowLog('RejectedTraffic', {
        destination: ec2.FlowLogDestination.toCloudWatchLogs(flowLogGroup),
        trafficType: ec2.FlowLogTrafficType.REJECT,
      });
    }

    /* ------------------------------ Security groups ----------------------------- */

    this.lambdaSecurityGroup = new ec2.SecurityGroup(this, 'LambdaSecurityGroup', {
      vpc: this.vpc,
      description: 'Zladovce Lambda functions in the VPC',
      allowAllOutbound: false,
    });
    this.databaseSecurityGroup = new ec2.SecurityGroup(this, 'DatabaseSecurityGroup', {
      vpc: this.vpc,
      description: 'Zladovce PostgreSQL instance',
      allowAllOutbound: false,
    });

    this.lambdaSecurityGroup.addEgressRule(
      ec2.Peer.anyIpv4(),
      ec2.Port.tcp(443),
      'HTTPS to AWS APIs (through NAT or the S3 gateway endpoint)',
    );
    this.lambdaSecurityGroup.addEgressRule(
      this.databaseSecurityGroup,
      ec2.Port.tcp(5432),
      'PostgreSQL to the database',
    );
    this.databaseSecurityGroup.addIngressRule(
      this.lambdaSecurityGroup,
      ec2.Port.tcp(5432),
      'PostgreSQL from Lambda functions',
    );

    if (config.database.useRdsProxy) {
      this.proxySecurityGroup = new ec2.SecurityGroup(this, 'ProxySecurityGroup', {
        vpc: this.vpc,
        description: 'Zladovce RDS Proxy',
        allowAllOutbound: false,
      });
      this.lambdaSecurityGroup.addEgressRule(this.proxySecurityGroup, ec2.Port.tcp(5432), 'PostgreSQL to RDS Proxy');
      this.proxySecurityGroup.addIngressRule(
        this.lambdaSecurityGroup,
        ec2.Port.tcp(5432),
        'PostgreSQL from Lambda functions',
      );
      this.proxySecurityGroup.addEgressRule(
        this.databaseSecurityGroup,
        ec2.Port.tcp(5432),
        'PostgreSQL to the database',
      );
      this.databaseSecurityGroup.addIngressRule(
        this.proxySecurityGroup,
        ec2.Port.tcp(5432),
        'PostgreSQL from RDS Proxy',
      );
    }
  }
}

import { Match } from 'aws-cdk-lib/assertions';
import { onlyResource, synth } from './helpers';

describe('network (default: natMode=instance)', () => {
  const { network } = synth('dev');

  test('VPC with two public and two private subnets in two availability zones', () => {
    network.resourceCountIs('AWS::EC2::VPC', 1);
    network.resourceCountIs('AWS::EC2::Subnet', 4);
    const zones = Object.values(network.findResources('AWS::EC2::Subnet')).map(
      (subnet: any) => subnet.Properties.AvailabilityZone,
    );
    expect(new Set(zones)).toEqual(new Set(['eu-central-1a', 'eu-central-1b']));
  });

  test('no managed NAT gateway in the default mode', () => {
    network.resourceCountIs('AWS::EC2::NatGateway', 0);
    network.resourceCountIs('AWS::EC2::EIP', 0);
  });

  test('one t4g.nano NAT instance with source/destination check disabled', () => {
    network.resourceCountIs('AWS::EC2::Instance', 1);
    network.hasResourceProperties('AWS::EC2::Instance', {
      InstanceType: 't4g.nano',
      SourceDestCheck: false,
    });
  });

  test('NAT instance accepts only HTTPS from inside the VPC', () => {
    const groups = Object.values(network.findResources('AWS::EC2::SecurityGroup')) as any[];
    const nat = groups.find((group) => /NAT/i.test(group.Properties.GroupDescription));
    expect(nat).toBeDefined();
    expect(nat.Properties.SecurityGroupIngress).toHaveLength(1);
    expect(nat.Properties.SecurityGroupIngress[0]).toMatchObject({ IpProtocol: 'tcp', FromPort: 443, ToPort: 443 });
    expect(nat.Properties.SecurityGroupIngress[0].CidrIp).not.toBe('0.0.0.0/0');
  });

  test('private subnets route outbound traffic through the NAT instance', () => {
    network.hasResourceProperties('AWS::EC2::Route', {
      DestinationCidrBlock: '0.0.0.0/0',
      InstanceId: Match.anyValue(),
    });
  });

  test('S3 gateway endpoint', () => {
    network.hasResourceProperties('AWS::EC2::VPCEndpoint', {
      VpcEndpointType: 'Gateway',
      ServiceName: Match.objectLike({ 'Fn::Join': Match.anyValue() }),
    });
  });

  test('database security group accepts PostgreSQL from the Lambda security group only', () => {
    const groups = network.findResources('AWS::EC2::SecurityGroup');
    const databaseId = Object.keys(groups).find((id) => id.startsWith('DatabaseSecurityGroup'))!;
    const lambdaId = Object.keys(groups).find((id) => id.startsWith('LambdaSecurityGroup'))!;
    expect(databaseId).toBeDefined();
    expect(lambdaId).toBeDefined();

    // No inline ingress and exactly one standalone ingress rule in the whole stack.
    expect(groups[databaseId].Properties.SecurityGroupIngress).toBeUndefined();
    const { resource: ingress } = onlyResource(network, 'AWS::EC2::SecurityGroupIngress');
    expect(ingress.Properties).toMatchObject({
      IpProtocol: 'tcp',
      FromPort: 5432,
      ToPort: 5432,
      GroupId: { 'Fn::GetAtt': [databaseId, 'GroupId'] },
      SourceSecurityGroupId: { 'Fn::GetAtt': [lambdaId, 'GroupId'] },
    });
  });

  test('Lambda security group has no open-ended egress', () => {
    const groups = network.findResources('AWS::EC2::SecurityGroup');
    const lambdaId = Object.keys(groups).find((id) => id.startsWith('LambdaSecurityGroup'))!;
    const inlineEgress = groups[lambdaId].Properties.SecurityGroupEgress as any[];
    expect(inlineEgress).toEqual([
      expect.objectContaining({ CidrIp: '0.0.0.0/0', IpProtocol: 'tcp', FromPort: 443, ToPort: 443 }),
    ]);
  });

  test('no flow logs outside production, flow logs in production', () => {
    network.resourceCountIs('AWS::EC2::FlowLog', 0);
    synth('prod').network.resourceCountIs('AWS::EC2::FlowLog', 1);
  });
});

describe('network (natMode=gateway)', () => {
  const { network, observability } = synth('dev', { network: { natMode: 'gateway' } });

  test('one managed NAT gateway and no NAT instance', () => {
    network.resourceCountIs('AWS::EC2::NatGateway', 1);
    network.resourceCountIs('AWS::EC2::Instance', 0);
  });

  test('no NAT instance alarm', () => {
    const alarms = Object.values(observability.findResources('AWS::CloudWatch::Alarm')) as any[];
    expect(alarms.some((alarm) => alarm.Properties.Namespace === 'AWS/EC2')).toBe(false);
  });
});

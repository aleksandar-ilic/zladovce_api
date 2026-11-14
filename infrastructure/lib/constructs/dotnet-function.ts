import { ArnFormat, Duration, RemovalPolicy, Stack } from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import { Construct } from 'constructs';
import { resolveLambdaCode } from '../artifacts';
import { DOTNET_LAMBDA_RUNTIME } from '../config';

export interface DotnetFunctionProps {
  readonly functionName: string;
  readonly description: string;
  /** Lambda handler string. */
  readonly handler: string;
  /** Publish directory (or .zip), relative to `infrastructure/` unless absolute. */
  readonly assetPath: string;
  /** Fail synthesis when the artifact is missing instead of using the placeholder. */
  readonly requireArtifacts: boolean;
  readonly memoryMb: number;
  readonly timeout: Duration;
  readonly reservedConcurrency?: number;
  readonly logRetentionDays: number;
  readonly retainLogs: boolean;
  readonly environment: Record<string, string>;
  readonly vpc: ec2.IVpc;
  readonly securityGroup: ec2.ISecurityGroup;
  /** Extra least-privilege statements for the execution role. */
  readonly policyStatements?: iam.PolicyStatement[];
}

/**
 * A .NET Lambda function (arm64) inside the VPC with:
 *  - its own log group with retention,
 *  - its own execution role built from explicit statements (no AWS managed policies),
 *  - code from a backend publish directory, or the placeholder when that is missing.
 *
 * Reused for the API and the migration runner; later phases reuse it for the
 * outbox worker.
 */
export class DotnetFunction extends Construct {
  public readonly function: lambda.Function;
  public readonly role: iam.Role;
  public readonly logGroup: logs.LogGroup;
  public readonly usesPlaceholderCode: boolean;
  /** The literal (non-token) function name. */
  public readonly functionName: string;

  constructor(scope: Construct, id: string, props: DotnetFunctionProps) {
    super(scope, id);
    const stack = Stack.of(this);
    this.functionName = props.functionName;

    this.logGroup = new logs.LogGroup(this, 'LogGroup', {
      logGroupName: `/aws/lambda/${props.functionName}`,
      retention: props.logRetentionDays as logs.RetentionDays,
      removalPolicy: props.retainLogs ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });

    // Policies are inline in the role so that they exist before the function is created
    // (a VPC function cannot be created until its role may manage network interfaces).
    this.role = new iam.Role(this, 'Role', {
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
      description: `Execution role of ${props.functionName}`,
      inlinePolicies: {
        Logging: new iam.PolicyDocument({
          statements: [
            new iam.PolicyStatement({
              sid: 'WriteOwnLogs',
              actions: ['logs:CreateLogStream', 'logs:PutLogEvents'],
              resources: [
                stack.formatArn({
                  service: 'logs',
                  resource: 'log-group',
                  resourceName: `/aws/lambda/${props.functionName}:*`,
                  arnFormat: ArnFormat.COLON_RESOURCE_NAME,
                }),
              ],
            }),
          ],
        }),
        VpcNetworking: new iam.PolicyDocument({
          statements: [
            // UNAVOIDABLE WILDCARD: Lambda's VPC networking uses these EC2 actions, which do not
            // support resource-level permissions (same content as AWSLambdaVPCAccessExecutionRole,
            // minus its account-wide CloudWatch Logs access).
            new iam.PolicyStatement({
              sid: 'ManageVpcNetworkInterfaces',
              actions: [
                'ec2:CreateNetworkInterface',
                'ec2:DescribeNetworkInterfaces',
                'ec2:DescribeSubnets',
                'ec2:DeleteNetworkInterface',
                'ec2:AssignPrivateIpAddresses',
                'ec2:UnassignPrivateIpAddresses',
              ],
              resources: ['*'],
            }),
          ],
        }),
        ...(props.policyStatements && props.policyStatements.length > 0
          ? { Application: new iam.PolicyDocument({ statements: props.policyStatements }) }
          : {}),
      },
    });

    const resolution = resolveLambdaCode(this, props.functionName, props.assetPath, props.requireArtifacts);
    this.usesPlaceholderCode = resolution.isPlaceholder;

    this.function = new lambda.Function(this, 'Function', {
      functionName: props.functionName,
      description: resolution.isPlaceholder
        ? `PLACEHOLDER CODE - NOT DEPLOYABLE. ${props.description}`
        : props.description,
      runtime: DOTNET_LAMBDA_RUNTIME,
      architecture: lambda.Architecture.ARM_64,
      handler: props.handler,
      code: resolution.code,
      memorySize: props.memoryMb,
      timeout: props.timeout,
      reservedConcurrentExecutions: props.reservedConcurrency,
      role: this.role,
      logGroup: this.logGroup,
      vpc: props.vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [props.securityGroup],
      environment: props.environment,
    });
  }
}


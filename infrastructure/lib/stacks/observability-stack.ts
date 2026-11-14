import { CfnOutput, Duration, Stack, StackProps } from 'aws-cdk-lib';
import * as apigwv2 from 'aws-cdk-lib/aws-apigatewayv2';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as cloudwatchActions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as subscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import { Construct } from 'constructs';
import { EnvironmentConfig, resourcePrefix } from '../config';

export interface ObservabilityStackProps extends StackProps {
  readonly config: EnvironmentConfig;
  readonly httpApi: apigwv2.IHttpApi;
  readonly apiFunction: lambda.IFunction;
  readonly migrationFunction: lambda.IFunction;
  readonly imageProcessorFunction: lambda.IFunction;
  readonly imageProcessorDeadLetterQueue: sqs.IQueue;
  readonly databaseInstance: rds.IDatabaseInstance;
  /** Empty when `natMode` is `gateway`. */
  readonly natInstanceIds: string[];
}

/**
 * SNS alarm topic and CloudWatch alarms.
 *
 * Extension point (later phases): outbox alarms (dead rows, oldest pending
 * e-mail), SES bounce/complaint alarms and the AWS Budget alert are added here
 * with `this.addAlarm(...)`.
 */
export class ObservabilityStack extends Stack {
  public readonly alarmTopic: sns.Topic;
  private readonly prefix: string;

  constructor(scope: Construct, id: string, props: ObservabilityStackProps) {
    super(scope, id, props);
    const { config } = props;
    const thresholds = config.observability;
    this.prefix = resourcePrefix(config);

    this.alarmTopic = new sns.Topic(this, 'AlarmTopic', {
      topicName: `${this.prefix}-alarms`,
      displayName: `Zladovce ${config.envName} alarms`,
      enforceSSL: true,
    });
    if (thresholds.alarmEmail) {
      this.alarmTopic.addSubscription(new subscriptions.EmailSubscription(thresholds.alarmEmail));
    }

    const fiveMinutes = Duration.minutes(5);

    /* ---------------------------------- HTTP API --------------------------------- */

    this.addAlarm('Api5xx', {
      alarmDescription: 'HTTP API is returning 5xx responses',
      metric: props.httpApi.metricServerError({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: thresholds.api5xxThreshold,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('ApiLatencyP95', {
      alarmDescription: 'HTTP API p95 latency is high',
      metric: props.httpApi.metricLatency({ period: fiveMinutes, statistic: 'p95' }),
      threshold: thresholds.apiLatencyP95Ms,
      evaluationPeriods: 3,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
    });

    /* ----------------------------------- Lambda ---------------------------------- */

    this.addAlarm('ApiLambdaErrors', {
      alarmDescription: 'API Lambda invocations are failing',
      metric: props.apiFunction.metricErrors({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: thresholds.lambdaErrorThreshold,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('ApiLambdaThrottles', {
      alarmDescription: 'API Lambda is being throttled (reserved concurrency reached)',
      metric: props.apiFunction.metricThrottles({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('MigrationLambdaErrors', {
      alarmDescription: 'Database migration runner failed',
      metric: props.migrationFunction.metricErrors({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });

    /* ------------------------------ Image processor ------------------------------ */

    this.addAlarm('ImageProcessorErrors', {
      alarmDescription: 'Image processor Lambda invocations are failing (uploads are retried, then sent to the dead-letter queue)',
      metric: props.imageProcessorFunction.metricErrors({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: thresholds.lambdaErrorThreshold,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('ImageProcessorDeadLetters', {
      alarmDescription:
        'An upload could not be processed after all retries (event in the image processor dead-letter queue; the ' +
        'administrator sees IMAGE_NOT_READY). Inspect the message, fix the cause, then redrive or delete it.',
      metric: props.imageProcessorDeadLetterQueue.metricApproximateNumberOfMessagesVisible({
        period: fiveMinutes,
        statistic: 'Maximum',
      }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });

    /* ------------------------------------ RDS ------------------------------------ */

    this.addAlarm('RdsCpuHigh', {
      alarmDescription: 'RDS CPU utilisation is high',
      metric: props.databaseInstance.metricCPUUtilization({ period: fiveMinutes, statistic: 'Average' }),
      threshold: thresholds.rdsCpuPercent,
      evaluationPeriods: 3,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
    });
    this.addAlarm('RdsFreeStorageLow', {
      alarmDescription: 'RDS free storage is low',
      metric: props.databaseInstance.metricFreeStorageSpace({ period: fiveMinutes, statistic: 'Minimum' }),
      threshold: thresholds.rdsFreeStorageBytes,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.LESS_THAN_THRESHOLD,
    });
    this.addAlarm('RdsConnectionsHigh', {
      alarmDescription:
        'RDS connection count is high (trigger to consider useRdsProxy, see docs/AWS_INFRASTRUCTURE.md W4)',
      metric: props.databaseInstance.metricDatabaseConnections({ period: fiveMinutes, statistic: 'Maximum' }),
      threshold: thresholds.rdsMaxConnections,
      evaluationPeriods: 2,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
    });

    /* ------------------------------------ NAT ------------------------------------ */

    props.natInstanceIds.forEach((instanceId, index) => {
      this.addAlarm(`NatInstanceStatusCheck${index + 1}`, {
        alarmDescription: 'NAT instance failed its status check or stopped (outbound e-mail is delayed)',
        metric: new cloudwatch.Metric({
          namespace: 'AWS/EC2',
          metricName: 'StatusCheckFailed',
          dimensionsMap: { InstanceId: instanceId },
          period: fiveMinutes,
          statistic: 'Maximum',
        }),
        threshold: 1,
        evaluationPeriods: 2,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        // A stopped or terminated instance reports nothing at all.
        treatMissingData: cloudwatch.TreatMissingData.BREACHING,
      });
    });

    new CfnOutput(this, 'AlarmTopicArn', { value: this.alarmTopic.topicArn });
  }

  /** Creates an alarm that notifies the alarm topic on ALARM and on recovery. */
  public addAlarm(id: string, props: cloudwatch.AlarmProps): cloudwatch.Alarm {
    const alarm = new cloudwatch.Alarm(this, id, {
      alarmName: `${this.prefix}-${id}`,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      ...props,
    });
    const action = new cloudwatchActions.SnsAction(this.alarmTopic);
    alarm.addAlarmAction(action);
    alarm.addOkAction(action);
    return alarm;
  }
}

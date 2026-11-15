import { CfnOutput, Duration, Stack, StackProps } from 'aws-cdk-lib';
import * as apigwv2 from 'aws-cdk-lib/aws-apigatewayv2';
import * as budgets from 'aws-cdk-lib/aws-budgets';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as cloudwatchActions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as sqs from 'aws-cdk-lib/aws-sqs';
import * as subscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import { Construct } from 'constructs';
import { BudgetConfig, envTitle, EnvironmentConfig, OUTBOX_DEAD_LOG_TEXT, PROJECT_NAME, resourcePrefix } from '../config';
import { DotnetFunction } from '../constructs/dotnet-function';

export interface ObservabilityStackProps extends StackProps {
  readonly config: EnvironmentConfig;
  readonly httpApi: apigwv2.IHttpApi;
  readonly apiFunction: lambda.IFunction;
  readonly migrationFunction: lambda.IFunction;
  readonly imageProcessorFunction: lambda.IFunction;
  readonly imageProcessorDeadLetterQueue: sqs.IQueue;
  readonly outboxWorker: DotnetFunction;
  readonly databaseInstance: rds.IDatabaseInstance;
  /** Empty when `natMode` is `gateway`. */
  readonly natInstanceIds: string[];
}

/** Namespace of the custom metrics this stack derives from logs, e.g. `Zladovce/Prod`. */
export function customMetricNamespace(config: EnvironmentConfig): string {
  return `${PROJECT_NAME}/${envTitle(config.envName)}`;
}

/**
 * SNS alarm topic, CloudWatch alarms and (in the environment that has `budget`) the account's AWS Budget.
 *
 * Known gap: the backend publishes no metric for the outbox backlog (oldest Pending e-mail). The worker alarms below
 * (errors, throttles, duration, "not running") and the dead-e-mail log metric cover the failure modes it reports.
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

    /* ------------------------------- Outbox worker ------------------------------- */

    const worker = props.outboxWorker.function;
    const workerTimeoutSeconds = config.outboxWorker.timeoutSeconds;
    this.addAlarm('OutboxWorkerErrors', {
      alarmDescription:
        'E-mail outbox worker runs are failing (database or configuration problem; failed e-mails alone do not fail a ' +
        'run). E-mails wait in the outbox until it works again.',
      metric: worker.metricErrors({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: thresholds.lambdaErrorThreshold,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('OutboxWorkerThrottles', {
      alarmDescription:
        'E-mail outbox worker is throttled for 15 minutes (runs overlap their reserved concurrency of 1, or the ' +
        'account concurrency is exhausted). An occasional throttle after a long run is harmless.',
      metric: worker.metricThrottles({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: 1,
      evaluationPeriods: 3,
      datapointsToAlarm: 3,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('OutboxWorkerDurationNearTimeout', {
      alarmDescription:
        `E-mail outbox worker runs end within 5 s of their ${workerTimeoutSeconds} s timeout (slow SES or database). ` +
        'Rows of a run cut off by the timeout are retried after their lease expires.',
      metric: worker.metricDuration({ period: fiveMinutes, statistic: 'Maximum' }),
      threshold: (workerTimeoutSeconds - 5) * 1000,
      evaluationPeriods: 3,
      datapointsToAlarm: 2,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });
    this.addAlarm('OutboxWorkerNotRunning', {
      alarmDescription:
        'The e-mail outbox worker has not run for 15 minutes (schedule disabled or function broken): e-mails are not ' +
        'being sent and rate-limit windows are not purged.',
      metric: worker.metricInvocations({ period: Duration.minutes(15), statistic: 'Sum' }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.LESS_THAN_THRESHOLD,
      // No data at all means no invocation.
      treatMissingData: cloudwatch.TreatMissingData.BREACHING,
    });

    // The backend publishes no outbox metric. It does log every e-mail that becomes Dead after its last attempt
    // (EmailDispatcher: "Email {OutboxId} ({Template}) attempt {Attempt} failed; now Dead."), so count those lines.
    // Not counted: rows marked Dead because a worker died during their last attempt (only in the run summary line).
    const deadEmails = new logs.MetricFilter(this, 'OutboxDeadEmailsFilter', {
      logGroup: props.outboxWorker.logGroup,
      filterName: `${this.prefix}-outbox-dead-emails`,
      filterPattern: logs.FilterPattern.literal(`"${OUTBOX_DEAD_LOG_TEXT}"`),
      metricNamespace: customMetricNamespace(config),
      metricName: 'OutboxDeadEmails',
      metricValue: '1',
      unit: cloudwatch.Unit.COUNT,
    });
    this.addAlarm('OutboxDeadEmails', {
      alarmDescription:
        'An e-mail failed its last attempt and is Dead. See Admin > E-mails (status Dead) for the error, fix the cause ' +
        'and use Retry.',
      metric: deadEmails.metric({ period: fiveMinutes, statistic: 'Sum' }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
    });

    /* ------------------------------------ SES ------------------------------------ */

    // Reputation metrics are ACCOUNT-LEVEL (no dimension) and therefore shared by all environments of the account:
    // created in one environment only (observability.sesReputationAlarms, prod).
    if (thresholds.sesReputationAlarms) {
      const oneHour = Duration.hours(1);
      this.addAlarm('SesBounceRateHigh', {
        alarmDescription:
          'SES account bounce rate is high (whole account, all environments). SES reviews accounts at 5 % and may ' +
          'pause sending at 10 %. Check the suppression list and recent bounces.',
        metric: new cloudwatch.Metric({
          namespace: 'AWS/SES',
          metricName: 'Reputation.BounceRate',
          period: oneHour,
          statistic: 'Maximum',
        }),
        threshold: thresholds.sesBounceRateThreshold,
        evaluationPeriods: 1,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      });
      this.addAlarm('SesComplaintRateHigh', {
        alarmDescription:
          'SES account complaint rate is high (whole account, all environments). SES reviews accounts at 0.1 % and ' +
          'may pause sending at 0.5 %.',
        metric: new cloudwatch.Metric({
          namespace: 'AWS/SES',
          metricName: 'Reputation.ComplaintRate',
          period: oneHour,
          statistic: 'Maximum',
        }),
        threshold: thresholds.sesComplaintRateThreshold,
        evaluationPeriods: 1,
        comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      });
    }

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

    /* ----------------------------------- Budget ---------------------------------- */

    if (config.budget) {
      this.createBudget(config.budget);
    }

    new CfnOutput(this, 'AlarmTopicArn', { value: this.alarmTopic.topicArn });
  }

  /**
   * Monthly cost budget of the WHOLE ACCOUNT (the three environments share one account), notifying the alarm topic and
   * the configured addresses. Exists in the environment whose configuration has `budget` (prod) only.
   */
  private createBudget(budget: BudgetConfig): void {
    // AWS Budgets publishes to the topic as the budgets service, from this account only.
    this.alarmTopic.addToResourcePolicy(
      new iam.PolicyStatement({
        sid: 'AllowAwsBudgetsToPublish',
        principals: [new iam.ServicePrincipal('budgets.amazonaws.com')],
        actions: ['sns:Publish'],
        resources: [this.alarmTopic.topicArn],
        conditions: {
          StringEquals: { 'aws:SourceAccount': this.account },
          ArnLike: { 'aws:SourceArn': `arn:${this.partition}:budgets::${this.account}:*` },
        },
      }),
    );

    const subscribers: budgets.CfnBudget.SubscriberProperty[] = [
      { subscriptionType: 'SNS', address: this.alarmTopic.topicArn },
      ...budget.notificationEmails.map((address) => ({ subscriptionType: 'EMAIL', address })),
    ];
    const notification = (notificationType: 'ACTUAL' | 'FORECASTED', threshold: number) => ({
      notification: {
        notificationType,
        comparisonOperator: 'GREATER_THAN',
        threshold,
        thresholdType: 'PERCENTAGE',
      },
      subscribers,
    });

    const cfnBudget = new budgets.CfnBudget(this, 'AccountMonthlyBudget', {
      budget: {
        budgetName: `${PROJECT_NAME.toLowerCase()}-account-monthly-cost`,
        budgetType: 'COST',
        timeUnit: 'MONTHLY',
        budgetLimit: { amount: budget.monthlyLimitUsd, unit: 'USD' },
      },
      notificationsWithSubscribers: [
        ...budget.actualThresholdsPercent.map((threshold) => notification('ACTUAL', threshold)),
        ...(budget.forecastThresholdPercent ? [notification('FORECASTED', budget.forecastThresholdPercent)] : []),
      ],
    });
    // The topic policy must exist before Budgets validates the SNS subscriber.
    const topicPolicy = this.alarmTopic.node.tryFindChild('Policy');
    if (topicPolicy) cfnBudget.node.addDependency(topicPolicy);
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

import { Validations } from 'aws-cdk-lib';
import { IConstruct } from 'constructs';
import { envTitle, MEDIA_PREFIX, PROJECT_NAME, UPLOADS_PREFIX } from './config';
import type { ZladovceEnvironment } from './zladovce-environment';

/**
 * cdk-nag (AwsSolutions pack) acknowledgments, each with a written reason.
 *
 * The unit test `test/nag.test.ts` runs the pack against every environment and
 * fails on any finding that is not acknowledged here, so a new resource that
 * violates a rule must either be fixed or consciously added to this file.
 */

function acknowledge(scope: IConstruct, id: string, reason: string): void {
  Validations.of(scope).acknowledge({ id, reason });
}

export function applyNagSuppressions(environment: ZladovceEnvironment): void {
  const { config, network, database, auth, media, migrations, notifications, api } = environment;

  /* --------------------------------- Network --------------------------------- */

  if (config.network.natMode === 'instance') {
    acknowledge(
      network,
      'AwsSolutions-EC23',
      'The rule cannot evaluate a tokenised CIDR. The NAT security group only allows TCP 443 inbound from the VPC CIDR.',
    );
    acknowledge(
      network,
      'AwsSolutions-EC26',
      'The NAT instance is stateless and stores no data; its root volume only holds the Amazon Linux image.',
    );
    acknowledge(
      network,
      'AwsSolutions-EC28',
      'Detailed monitoring is a paid feature; the status-check alarm on the NAT instance uses basic monitoring.',
    );
    acknowledge(
      network,
      'AwsSolutions-EC29',
      'The NAT instance is stateless and is intentionally replaced by CloudFormation when a newer AMI is released.',
    );
  }
  if (!config.network.flowLogs) {
    acknowledge(
      network,
      'AwsSolutions-VPC7',
      'VPC flow logs are a per-environment switch (network.flowLogs); enabled in production, off elsewhere to save cost.',
    );
  }

  /* --------------------------------- Database -------------------------------- */

  acknowledge(
    database,
    'AwsSolutions-RDS3',
    'Single-AZ by design (docs/AWS_INFRASTRUCTURE.md W3/W10): Multi-AZ doubles the largest fixed cost for a small shop.',
  );
  acknowledge(
    database,
    'AwsSolutions-RDS11',
    'The instance is in private subnets and only reachable from the Lambda security group; a non-default port adds nothing.',
  );
  if (!config.database.deletionProtection) {
    acknowledge(
      database,
      'AwsSolutions-RDS10',
      'Deletion protection is enabled in production only; non-production databases hold test data and must be disposable.',
    );
  }

  /* ----------------------------------- Auth ---------------------------------- */

  acknowledge(
    auth,
    'AwsSolutions-COG2',
    'TOTP MFA is optional by design (docs/ARCHITECTURE.md A6): recommended for Owner, not forced on Staff.',
  );
  acknowledge(
    auth,
    'AwsSolutions-COG8',
    'The Plus feature plan is a paid tier; the pool holds a handful of invited administrators with a strong password policy.',
  );

  /* ---------------------------------- Media ---------------------------------- */

  acknowledge(
    media,
    'AwsSolutions-S1',
    'Server access logs are not enabled: the bucket is private, reachable only through CloudFront (media/*) and the API role.',
  );
  acknowledge(
    media,
    'AwsSolutions-CFR1',
    'No geo restriction: product images are public content for any visitor.',
  );
  acknowledge(
    media,
    'AwsSolutions-CFR2',
    'No WAF: the distribution serves static, public, immutable images only (GET/HEAD); WAF would add a fixed monthly cost.',
  );
  acknowledge(
    media,
    'AwsSolutions-CFR3',
    'CloudFront access logs are not enabled for static public images; revisit together with analytics requirements.',
  );
  if (!config.domains.media) {
    acknowledge(
      media,
      'AwsSolutions-CFR4',
      'Without a custom domain the distribution must use the default CloudFront certificate, whose TLS policy cannot be ' +
        'changed. TLSv1.2_2021 is applied as soon as domains.media is configured.',
    );
  }
  const autoDelete = config.media.autoDeleteObjects && !config.media.retainOnDelete;
  acknowledge(
    media,
    'AwsSolutions-IAM4[Policy::arn:<AWS::Partition>:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole]',
    'Roles of CDK-owned custom resources: the bucket-notifications handler that subscribes the image processor to ' +
      'uploads/ (S3 notifications to a Lambda need it to avoid a bucket/permission dependency cycle)' +
      (autoDelete ? ', and the auto-delete-objects resource (throw-away environments only).' : '.'),
  );
  if (autoDelete) {
    acknowledge(
      media,
      'AwsSolutions-L1',
      'Runtime of the CDK-owned auto-delete-objects custom resource is chosen by aws-cdk-lib.',
    );
  }
  acknowledge(
    media.imageProcessorDeadLetterQueue,
    'AwsSolutions-SQS3',
    'This queue is itself the dead-letter destination (Lambda on-failure destination) of the image processor; ' +
      'a dead-letter queue of the dead-letter queue adds nothing. Messages are kept 14 days and alarmed on.',
  );

  /* ------------------------------- Notifications ------------------------------ */

  acknowledge(
    notifications.trackingKeySecret,
    'AwsSolutions-SMG4',
    'The tracking-code key encrypts codes inside queued outbox rows, and the backend reads one key (no key versions). ' +
      'Automatic rotation would make every queued e-mail undecryptable. Rotate by hand only with an empty outbox.',
  );

  /* ----------------------------------- API ----------------------------------- */

  for (const route of api.unauthenticatedRoutes) {
    acknowledge(
      route,
      'AwsSolutions-APIG4',
      'Intentionally public: the storefront API is used by guests, and CORS preflight requests carry no credentials. ' +
        'Every /api/v1/admin/* method other than OPTIONS has the Cognito JWT authorizer.',
    );
  }

  // How cdk-nag renders the cross-stack import of the media bucket ARN in a finding id. The export name is
  // derived from construct ids in MediaStack; if those change, test/nag.test.ts fails and prints the new id.
  const mediaBucketArnExport = `${PROJECT_NAME}-${envTitle(config.envName)}-Media:ExportsOutputFnGetAttMediaBucketBCBB02BAArnB94B784B`;

  for (const fn of [api.apiFunction, migrations.migrationFunction, api.outboxWorker]) {
    acknowledge(
      fn.role,
      'AwsSolutions-IAM5[Resource::*]',
      'Lambda VPC networking (ec2:CreateNetworkInterface etc.) does not support resource-level permissions.',
    );
  }
  // The image processor runs outside the VPC and therefore has no Resource::* statement.
  for (const fn of [api.apiFunction, migrations.migrationFunction, media.imageProcessor, api.outboxWorker]) {
    acknowledge(
      fn.role,
      `AwsSolutions-IAM5[Resource::arn:aws:logs:${config.region}:<AWS::AccountId>:log-group:/aws/lambda/${fn.functionName}:*]`,
      'Scoped to the log streams of the log group that belongs to this function.',
    );
  }
  acknowledge(
    api.apiFunction.role,
    `AwsSolutions-IAM5[Resource::${mediaBucketArnExport}/${UPLOADS_PREFIX}*]`,
    'Object-level access limited to the uploads/ prefix of the media bucket (pre-signed uploads).',
  );
  acknowledge(
    api.apiFunction.role,
    `AwsSolutions-IAM5[Resource::${mediaBucketArnExport}/${MEDIA_PREFIX}*]`,
    'Object-level access limited to the media/ prefix of the media bucket (read and delete processed images).',
  );

  // The image processor is in the same stack as the bucket, so the ARN is a local reference.
  acknowledge(
    media.imageProcessor.role,
    `AwsSolutions-IAM5[Resource::<MediaBucketBCBB02BA.Arn>/${UPLOADS_PREFIX}*]`,
    'Object-level access limited to the uploads/ prefix (read the new upload, delete it once processed).',
  );
  acknowledge(
    media.imageProcessor.role,
    `AwsSolutions-IAM5[Resource::<MediaBucketBCBB02BA.Arn>/${MEDIA_PREFIX}*]`,
    'Object-level access limited to the media/ prefix (write renditions and the manifest, read the manifest).',
  );

}

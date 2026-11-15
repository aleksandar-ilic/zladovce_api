import { ArnFormat, CfnOutput, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import * as route53 from 'aws-cdk-lib/aws-route53';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import * as ses from 'aws-cdk-lib/aws-ses';
import { Construct } from 'constructs';
import {
  configurationSetName,
  emailIdentityDomain,
  EnvironmentConfig,
  resourcePrefix,
  TRACKING_KEY_PLACEHOLDER_MARKER,
} from '../config';

export interface NotificationsStackProps extends StackProps {
  readonly config: EnvironmentConfig;
}

/** Dimension of the SES CloudWatch event destination: SES's auto-tag with the configuration set name. */
export const SES_CONFIGURATION_SET_DIMENSION = 'ses:configuration-set';

/**
 * Everything the API and the outbox worker need for tracking codes and e-mail that is not code:
 *
 *  - the tracking-key secret (AES-256-GCM key that encrypts plain tracking codes inside outbox rows),
 *  - the SES domain identity (only in the environment that owns it, see `SesIdentityConfig`),
 *  - the environment's SES configuration set with reputation metrics, account suppression of bounces/complaints and a
 *    CloudWatch event destination (per-environment Send / Delivery / Bounce / Complaint / Reject metrics).
 *
 * The stack has no dependencies, so it can be deployed (and the secret initialised) before the API stack, and prod's
 * identity can be created before staging, which imports it.
 */
export class NotificationsStack extends Stack {
  public readonly trackingKeySecret: secretsmanager.Secret;
  /** Literal (non-token) secret name, e.g. `zladovce/dev/tracking-token-key`. */
  public readonly trackingKeySecretName: string;
  /** Set when the environment has an SES identity (created or imported). */
  public readonly emailIdentity?: ses.IEmailIdentity;
  /** ARN of the identity, built from literals (no cross-stack export). */
  public readonly emailIdentityArn?: string;
  public readonly configurationSet?: ses.ConfigurationSet;
  public readonly configurationSetName?: string;
  public readonly configurationSetArn?: string;

  constructor(scope: Construct, id: string, props: NotificationsStackProps) {
    super(scope, id, props);
    const { config } = props;
    const prefix = resourcePrefix(config);

    /* ----------------------------- Tracking-code key ----------------------------- */

    // Secrets Manager cannot generate "base64 of exactly 32 bytes" (it only generates passwords from a character set),
    // and a custom resource that does would add a Lambda function of its own. The secret is therefore created with a
    // marker value the backend rejects, and the real key is written ONCE per environment:
    //   aws secretsmanager put-secret-value --secret-id zladovce/<env>/tracking-token-key \
    //     --secret-string "$(openssl rand -base64 32)"
    // deploy.yml does that automatically while the marker is still there, and refuses to deploy the API while the
    // value is not a valid key. The key never appears in the template. CloudFormation only generates the value on
    // CREATE; DO NOT change `generateSecretString` below: an update of that property makes CloudFormation generate a new
    // value, which would overwrite the real key (queued e-mails could no longer be decrypted).
    this.trackingKeySecretName = `zladovce/${config.envName}/tracking-token-key`;
    this.trackingKeySecret = new secretsmanager.Secret(this, 'TrackingKeySecret', {
      secretName: this.trackingKeySecretName,
      description:
        `Zladovce ${config.envName}: AES-256 key for tracking codes in e-mail outbox rows. Value: base64 of 32 random ` +
        'bytes (or {"key":"<base64>"}). Set once with put-secret-value, see infrastructure/README.md. Never rotate ' +
        'automatically: queued e-mails are encrypted with the current key.',
      generateSecretString: {
        secretStringTemplate: JSON.stringify({ status: TRACKING_KEY_PLACEHOLDER_MARKER }),
        generateStringKey: 'filler',
        excludePunctuation: true,
        passwordLength: 32,
      },
      removalPolicy: config.tracking.retainKeyOnDelete ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
    });

    new CfnOutput(this, 'TrackingKeySecretName', {
      value: this.trackingKeySecretName,
      description: 'Secret with the tracking-code key; must hold base64 of 32 random bytes before the API is deployed',
    });
    new CfnOutput(this, 'TrackingKeySecretArn', { value: this.trackingKeySecret.secretArn });

    /* ------------------------------------ SES ------------------------------------ */

    const identity = config.email.identity;
    const domain = emailIdentityDomain(config);
    if (domain === undefined) return;

    if (identity.mode === 'create') {
      const zone = identity.hostedZone
        ? route53.PublicHostedZone.fromPublicHostedZoneAttributes(this, 'EmailHostedZone', {
            hostedZoneId: identity.hostedZone.hostedZoneId,
            zoneName: identity.hostedZone.zoneName,
          })
        : undefined;
      const created = new ses.EmailIdentity(this, 'EmailIdentity', {
        identity: zone ? ses.Identity.publicHostedZone(zone) : ses.Identity.domain(domain),
        dkimIdentity: ses.DkimIdentity.easyDkim(ses.EasyDkimSigningKeyLength.RSA_2048_BIT),
        dkimSigning: true,
        mailFromDomain: identity.mailFromDomain,
        // Without the MX record SES falls back to its own MAIL FROM domain instead of refusing to send.
        mailFromBehaviorOnMxFailure: identity.mailFromDomain
          ? ses.MailFromBehaviorOnMxFailure.USE_DEFAULT_VALUE
          : undefined,
        // SES only allows switching e-mail feedback off when identity-level SNS notifications are configured, which
        // this app does not do: bounces/complaints are acted on by the account suppression list (configuration set),
        // counted by the CloudWatch event destination and alarmed on through the reputation metrics.
        feedbackForwarding: true,
      });
      this.emailIdentity = created;

      if (!zone) {
        created.dkimRecords.forEach((record, index) => {
          new CfnOutput(this, `DkimRecord${index + 1}`, {
            value: `${record.name} CNAME ${record.value}`,
            description: `DKIM record ${index + 1} of 3 for ${domain}: add it at the DNS provider (CNAME)`,
          });
        });
      }
      if (identity.mailFromDomain) {
        new CfnOutput(this, 'MailFromMxRecord', {
          value: `${identity.mailFromDomain} MX 10 feedback-smtp.${this.region}.amazonses.com`,
          description: 'Custom MAIL FROM: add this MX record at the DNS provider',
        });
        new CfnOutput(this, 'MailFromSpfRecord', {
          value: `${identity.mailFromDomain} TXT "v=spf1 include:amazonses.com ~all"`,
          description: 'Custom MAIL FROM: add this SPF (TXT) record at the DNS provider',
        });
      }
      new CfnOutput(this, 'DmarcRecordSuggestion', {
        value: `_dmarc.${domain} TXT "v=DMARC1; p=none; rua=mailto:<your-address>"`,
        description: 'Recommended DMARC record (start with p=none, tighten later); not created by this app',
      });
    } else {
      // Owned by another environment of the same account (an identity exists once per account and region).
      this.emailIdentity = ses.EmailIdentity.fromEmailIdentityName(this, 'EmailIdentity', domain);
    }

    // Built from literals so that consumers need no cross-stack export (and the import mode needs no lookup).
    this.emailIdentityArn = this.formatArn({
      service: 'ses',
      resource: 'identity',
      resourceName: domain,
      arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
    });

    const setName = configurationSetName(config)!;
    this.configurationSetName = setName;
    this.configurationSet = new ses.ConfigurationSet(this, 'EmailConfigurationSet', {
      configurationSetName: setName,
      reputationMetrics: true,
      sendingEnabled: true,
      suppressionReasons: ses.SuppressionReasons.BOUNCES_AND_COMPLAINTS,
      tlsPolicy: ses.ConfigurationSetTlsPolicy.OPTIONAL,
    });
    this.configurationSet.addEventDestination('CloudWatchEvents', {
      configurationSetEventDestinationName: `${prefix}-cloudwatch`,
      destination: ses.EventDestination.cloudWatchDimensions([
        {
          name: SES_CONFIGURATION_SET_DIMENSION,
          source: ses.CloudWatchDimensionSource.MESSAGE_TAG,
          defaultValue: setName,
        },
      ]),
      events: [
        ses.EmailSendingEvent.SEND,
        ses.EmailSendingEvent.DELIVERY,
        ses.EmailSendingEvent.BOUNCE,
        ses.EmailSendingEvent.COMPLAINT,
        ses.EmailSendingEvent.REJECT,
        ses.EmailSendingEvent.DELIVERY_DELAY,
      ],
    });
    this.configurationSetArn = this.formatArn({
      service: 'ses',
      resource: 'configuration-set',
      resourceName: setName,
      arnFormat: ArnFormat.SLASH_RESOURCE_NAME,
    });

    new CfnOutput(this, 'EmailIdentityName', {
      value: domain,
      description:
        identity.mode === 'create'
          ? 'SES domain identity owned by this environment'
          : 'SES domain identity owned by another environment of this account (imported by name)',
    });
    new CfnOutput(this, 'EmailConfigurationSetName', { value: setName });
  }
}

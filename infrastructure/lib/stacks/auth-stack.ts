import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import { Construct } from 'constructs';
import { EnvironmentConfig, resourcePrefix } from '../config';

export interface AuthStackProps extends StackProps {
  readonly config: EnvironmentConfig;
}

export const ADMIN_GROUPS = ['Owner', 'Staff'] as const;

/** Lifetime of the temporary password in an invitation (Cognito: 0-365 days). */
export const TEMPORARY_PASSWORD_VALIDITY_DAYS = 3;

export interface InvitationMessage {
  readonly subject: string;
  /** HTML; contains Cognito's placeholders `{username}` and `{####}` (temporary password). */
  readonly body: string;
}

/**
 * Serbian invitation e-mail Cognito sends on AdminCreateUser (the backend's "invite administrator" and the CLI command
 * that creates the first Owner). Cognito replaces `{username}` with the user name (here: the e-mail address, which is
 * the pool's username attribute) and `{####}` with the temporary password; both placeholders are mandatory.
 * `config.auth.adminUrl` is validated (no quotes / angle brackets), so it can be inserted into the HTML as is.
 */
export function invitationMessage(config: EnvironmentConfig): InvitationMessage {
  const adminUrl = config.auth.adminUrl;
  const environmentSuffix = config.isProduction ? '' : ` (${config.envName})`;
  const body = [
    '<p>Zdravo,</p>',
    `<p>dobili ste pristup administraciji prodavnice Zladovce${environmentSuffix}.</p>`,
    '<p>',
    `Adresa administracije: <a href="${adminUrl}">${adminUrl}</a><br>`,
    'Korisničko ime (vaša e-adresa): {username}<br>',
    'Privremena lozinka: <b>{####}</b>',
    '</p>',
    `<p>Pri prvoj prijavi biće zatraženo da postavite novu lozinku. Privremena lozinka važi ${TEMPORARY_PASSWORD_VALIDITY_DAYS} dana; ` +
      'ako istekne, obratite se vlasniku prodavnice za novu pozivnicu.</p>',
    '<p>Ako niste očekivali ovu poruku, slobodno je zanemarite.</p>',
  ].join('\n');
  return { subject: `Pozivnica za administraciju prodavnice Zladovce${environmentSuffix}`, body };
}

/**
 * Cognito user pool for administrators only. Customers are guests and never
 * authenticate. Admins are invited (no self sign-up) and sign in with e-mail
 * through the SRP flow from the admin SPA.
 */
export class AuthStack extends Stack {
  public readonly userPool: cognito.UserPool;
  public readonly adminClient: cognito.UserPoolClient;
  /** JWT issuer URL: https://cognito-idp.<region>.amazonaws.com/<poolId> */
  public readonly issuerUrl: string;

  constructor(scope: Construct, id: string, props: AuthStackProps) {
    super(scope, id, props);
    const { config } = props;
    const auth = config.auth;
    const invitation = invitationMessage(config);

    this.userPool = new cognito.UserPool(this, 'AdminUserPool', {
      userPoolName: `${resourcePrefix(config)}-admins`,
      selfSignUpEnabled: false,
      // E-mail only (no `username` alias) => the e-mail address IS the user name (UsernameAttributes: [email]). The
      // backend's AdminCreateUser passes the e-mail as Username, which this requires.
      signInAliases: { email: true },
      signInCaseSensitive: false,
      autoVerify: { email: true },
      standardAttributes: {
        email: { required: true, mutable: true },
      },
      passwordPolicy: {
        minLength: auth.passwordMinLength,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: true,
        tempPasswordValidity: Duration.days(TEMPORARY_PASSWORD_VALIDITY_DAYS),
      },
      mfa: cognito.Mfa.OPTIONAL,
      mfaSecondFactor: { otp: true, sms: false },
      accountRecovery: cognito.AccountRecovery.EMAIL_ONLY,
      deletionProtection: auth.retainOnDelete,
      removalPolicy: auth.retainOnDelete ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
      // Invitation e-mail (AdminCreateUser with DesiredDeliveryMediums EMAIL), in Serbian, with the admin URL.
      userInvitation: {
        emailSubject: invitation.subject,
        emailBody: invitation.body,
      },
      // E-mail is sent by Cognito's built-in sender (COGNITO_DEFAULT, no-reply@verificationemail.com, a small daily
      // quota per account - see README). Switching to SES (`email: cognito.UserPoolEmail.withSES(...)`) needs a
      // VERIFIED identity at deploy time, which would make the first deployment depend on the DNS records.
    });

    for (const groupName of ADMIN_GROUPS) {
      this.userPool.addGroup(`${groupName}Group`, {
        groupName,
        description: groupName === 'Owner' ? 'Vlasnik - full access' : 'Urednik - day-to-day administration',
      });
    }

    this.adminClient = this.userPool.addClient('AdminSpaClient', {
      userPoolClientName: `${resourcePrefix(config)}-admin-spa`,
      generateSecret: false,
      authFlows: { userSrp: true },
      preventUserExistenceErrors: true,
      enableTokenRevocation: true,
      accessTokenValidity: Duration.minutes(auth.accessTokenValidityMinutes),
      idTokenValidity: Duration.minutes(auth.idTokenValidityMinutes),
      refreshTokenValidity: Duration.hours(auth.refreshTokenValidityHours),
      // The admin app shows its own sign-in form; the hosted UI / OAuth flows are not used.
      disableOAuth: true,
    });

    this.issuerUrl = `https://cognito-idp.${this.region}.amazonaws.com/${this.userPool.userPoolId}`;

    new CfnOutput(this, 'UserPoolId', { value: this.userPool.userPoolId, description: 'Cognito user pool id' });
    new CfnOutput(this, 'UserPoolClientId', {
      value: this.adminClient.userPoolClientId,
      description: 'Cognito app client id of the admin SPA (public client, no secret)',
    });
    new CfnOutput(this, 'CognitoRegion', { value: this.region });
  }
}

import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import { Construct } from 'constructs';
import { EnvironmentConfig, resourcePrefix } from '../config';

export interface AuthStackProps extends StackProps {
  readonly config: EnvironmentConfig;
}

export const ADMIN_GROUPS = ['Owner', 'Staff'] as const;

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

    this.userPool = new cognito.UserPool(this, 'AdminUserPool', {
      userPoolName: `${resourcePrefix(config)}-admins`,
      selfSignUpEnabled: false,
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
        tempPasswordValidity: Duration.days(3),
      },
      mfa: cognito.Mfa.OPTIONAL,
      mfaSecondFactor: { otp: true, sms: false },
      accountRecovery: cognito.AccountRecovery.EMAIL_ONLY,
      deletionProtection: auth.retainOnDelete,
      removalPolicy: auth.retainOnDelete ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
      // E-mail is sent by Cognito's built-in sender for now. Switching to SES
      // (`email: cognito.UserPoolEmail.withSES(...)`) belongs to the phase that adds the SES identity.
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

import { Match } from 'aws-cdk-lib/assertions';
import { onlyResource, synth } from './helpers';

describe('auth (Cognito)', () => {
  const dev = synth('dev');
  const prod = synth('prod');

  test('self sign-up is disabled and sign-in is by e-mail', () => {
    dev.auth.hasResourceProperties('AWS::Cognito::UserPool', {
      AdminCreateUserConfig: { AllowAdminCreateUserOnly: true },
      UsernameAttributes: ['email'],
    });
  });

  test('strong password policy', () => {
    dev.auth.hasResourceProperties('AWS::Cognito::UserPool', {
      Policies: {
        PasswordPolicy: {
          MinimumLength: 12,
          RequireLowercase: true,
          RequireUppercase: true,
          RequireNumbers: true,
          RequireSymbols: true,
        },
      },
    });
  });

  test('optional TOTP MFA, no SMS', () => {
    dev.auth.hasResourceProperties('AWS::Cognito::UserPool', {
      MfaConfiguration: 'OPTIONAL',
      EnabledMfas: ['SOFTWARE_TOKEN_MFA'],
    });
  });

  test('groups Owner and Staff', () => {
    dev.auth.resourceCountIs('AWS::Cognito::UserPoolGroup', 2);
    dev.auth.hasResourceProperties('AWS::Cognito::UserPoolGroup', { GroupName: 'Owner' });
    dev.auth.hasResourceProperties('AWS::Cognito::UserPoolGroup', { GroupName: 'Staff' });
  });

  test('one public app client without secret, SRP flow, short-lived tokens', () => {
    const { resource } = onlyResource(dev.auth, 'AWS::Cognito::UserPoolClient');
    expect(resource.Properties.GenerateSecret).toBe(false);
    expect(resource.Properties.ExplicitAuthFlows).toEqual(
      expect.arrayContaining(['ALLOW_USER_SRP_AUTH', 'ALLOW_REFRESH_TOKEN_AUTH']),
    );
    expect(resource.Properties.ExplicitAuthFlows).not.toContain('ALLOW_USER_PASSWORD_AUTH');
    expect(resource.Properties.ExplicitAuthFlows).not.toContain('ALLOW_ADMIN_USER_PASSWORD_AUTH');
    expect(resource.Properties.PreventUserExistenceErrors).toBe('ENABLED');
    expect(resource.Properties.AccessTokenValidity).toBe(60);
    expect(resource.Properties.RefreshTokenValidity).toBe(720);
    expect(resource.Properties.TokenValidityUnits).toMatchObject({ AccessToken: 'minutes', RefreshToken: 'minutes' });
  });

  test('user pool is retained and protected in production only', () => {
    const prodPool = onlyResource(prod.auth, 'AWS::Cognito::UserPool').resource;
    expect(prodPool.DeletionPolicy).toBe('Retain');
    expect(prodPool.Properties.DeletionProtection).toBe('ACTIVE');

    const devPool = onlyResource(dev.auth, 'AWS::Cognito::UserPool').resource;
    expect(devPool.DeletionPolicy).toBe('Delete');
    expect(devPool.Properties.DeletionProtection).toBe('INACTIVE');
  });
});

describe('media (S3 + CloudFront)', () => {
  const dev = synth('dev');
  const prod = synth('prod');

  test('bucket blocks all public access, is encrypted and versioned', () => {
    for (const { media } of [dev, prod]) {
      media.resourceCountIs('AWS::S3::Bucket', 1);
      media.hasResourceProperties('AWS::S3::Bucket', {
        PublicAccessBlockConfiguration: {
          BlockPublicAcls: true,
          BlockPublicPolicy: true,
          IgnorePublicAcls: true,
          RestrictPublicBuckets: true,
        },
        BucketEncryption: {
          ServerSideEncryptionConfiguration: [{ ServerSideEncryptionByDefault: { SSEAlgorithm: 'AES256' } }],
        },
        VersioningConfiguration: { Status: 'Enabled' },
      });
    }
  });

  test('lifecycle: uploads/ expire after 1 day, noncurrent versions after 30 days', () => {
    dev.media.hasResourceProperties('AWS::S3::Bucket', {
      LifecycleConfiguration: {
        Rules: Match.arrayWith([
          Match.objectLike({ Prefix: 'uploads/', ExpirationInDays: 1, Status: 'Enabled' }),
          Match.objectLike({ NoncurrentVersionExpiration: { NoncurrentDays: 30 }, Status: 'Enabled' }),
        ]),
      },
    });
  });

  test('CORS allows PUT/POST from the admin origins only', () => {
    const devRules = onlyResource(dev.media, 'AWS::S3::Bucket').resource.Properties.CorsConfiguration.CorsRules;
    expect(devRules).toHaveLength(1);
    expect(devRules[0].AllowedMethods).toEqual(['PUT', 'POST']);
    expect(devRules[0].AllowedOrigins).toEqual(['http://localhost:4201']);

    const prodRules = onlyResource(prod.media, 'AWS::S3::Bucket').resource.Properties.CorsConfiguration.CorsRules;
    expect(prodRules[0].AllowedOrigins).toEqual(['https://admin.zladovce.com']);
  });

  test('bucket policy: TLS only, and CloudFront may read media/* of this distribution only', () => {
    const { resource } = onlyResource(dev.media, 'AWS::S3::BucketPolicy');
    const statements = resource.Properties.PolicyDocument.Statement as any[];

    const denyInsecure = statements.find(
      (s) => s.Effect === 'Deny' && s.Condition?.Bool?.['aws:SecureTransport'] === 'false',
    );
    expect(denyInsecure).toBeDefined();

    const cloudfront = statements.filter((s) => s.Principal?.Service === 'cloudfront.amazonaws.com');
    expect(cloudfront).toHaveLength(1);
    expect(cloudfront[0].Action).toBe('s3:GetObject');
    expect(JSON.stringify(cloudfront[0].Resource)).toContain('/media/*');
    expect(cloudfront[0].Condition.StringEquals['AWS:SourceArn']).toBeDefined();

    // Nobody gets anonymous access.
    expect(statements.some((s) => s.Effect === 'Allow' && (s.Principal === '*' || s.Principal?.AWS === '*'))).toBe(
      false,
    );
  });

  test('CloudFront uses Origin Access Control and HTTPS, and only media/* reaches the bucket', () => {
    dev.media.resourceCountIs('AWS::CloudFront::OriginAccessControl', 1);
    dev.media.hasResourceProperties('AWS::CloudFront::OriginAccessControl', {
      OriginAccessControlConfig: { OriginAccessControlOriginType: 's3', SigningBehavior: 'always', SigningProtocol: 'sigv4' },
    });

    const config = onlyResource(dev.media, 'AWS::CloudFront::Distribution').resource.Properties.DistributionConfig;
    expect(config.Origins).toHaveLength(1);
    expect(config.Origins[0].OriginAccessControlId).toBeDefined();
    expect(config.Origins[0].S3OriginConfig.OriginAccessIdentity).toBe('');

    // Default behaviour: rejected at the edge by a CloudFront Function.
    expect(config.DefaultCacheBehavior.ViewerProtocolPolicy).toBe('redirect-to-https');
    expect(config.DefaultCacheBehavior.FunctionAssociations).toHaveLength(1);
    expect(config.DefaultCacheBehavior.FunctionAssociations[0].EventType).toBe('viewer-request');

    expect(config.CacheBehaviors).toHaveLength(1);
    expect(config.CacheBehaviors[0].PathPattern).toBe('media/*');
    expect(config.CacheBehaviors[0].ViewerProtocolPolicy).toBe('redirect-to-https');
    expect(config.CacheBehaviors[0].AllowedMethods).toEqual(['GET', 'HEAD']);
  });

  test('long cache lifetime for media', () => {
    dev.media.hasResourceProperties('AWS::CloudFront::CachePolicy', {
      CachePolicyConfig: Match.objectLike({ DefaultTTL: 31536000, MaxTTL: 31536000 }),
    });
  });

  test('bucket is retained in production and disposable in dev', () => {
    expect(onlyResource(prod.media, 'AWS::S3::Bucket').resource.DeletionPolicy).toBe('Retain');
    expect(onlyResource(dev.media, 'AWS::S3::Bucket').resource.DeletionPolicy).toBe('Delete');
    prod.media.resourceCountIs('Custom::S3AutoDeleteObjects', 0);
  });

  test('custom media domain is applied when configured', () => {
    const { media } = synth('prod', {
      domains: {
        media: {
          domainName: 'media.example.test',
          certificateArn: 'arn:aws:acm:us-east-1:111111111111:certificate/00000000-0000-0000-0000-000000000000',
        },
      },
    });
    const config = onlyResource(media, 'AWS::CloudFront::Distribution').resource.Properties.DistributionConfig;
    expect(config.Aliases).toEqual(['media.example.test']);
    expect(config.ViewerCertificate.MinimumProtocolVersion).toBe('TLSv1.2_2021');
  });
});

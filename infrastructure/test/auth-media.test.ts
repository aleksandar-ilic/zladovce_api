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

  test('CORS allows POST (pre-signed form upload) from the admin origins only, any header', () => {
    const devRules = onlyResource(dev.media, 'AWS::S3::Bucket').resource.Properties.CorsConfiguration.CorsRules;
    expect(devRules).toHaveLength(1);
    expect(devRules[0].AllowedMethods).toEqual(['POST']);
    expect(devRules[0].AllowedOrigins).toEqual(['http://localhost:4201']);
    expect(devRules[0].AllowedHeaders).toEqual(['*']);

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

describe('image processor (S3 uploads/ -> media/)', () => {
  const dev = synth('dev');
  const prod = synth('prod');
  const functions = (template: typeof dev.media): any[] => Object.values(template.findResources('AWS::Lambda::Function'));
  const processor = (template: typeof dev.media, env: string): any =>
    functions(template).find((fn) => fn.Properties.FunctionName === `zladovce-${env}-image-processor`);
  const statements = (template: typeof dev.media): any[] => {
    const roles = template.findResources('AWS::IAM::Role');
    const id = Object.keys(roles).find((key) => key.startsWith('ImageProcessorRole'));
    if (!id) throw new Error('ImageProcessorRole not found');
    return (roles[id].Properties.Policies ?? []).flatMap((policy: any) => policy.PolicyDocument.Statement);
  };

  test('.NET 10 on arm64, 1024 MB, 60 s, handler from the Zladovce.Lambda package, not in the VPC', () => {
    const fn = processor(dev.media, 'dev');
    expect(fn.Properties).toMatchObject({
      Runtime: 'dotnet10',
      Architectures: ['arm64'],
      MemorySize: 1024,
      Timeout: 60,
      Handler: 'Zladovce.Lambda::Zladovce.Lambda.ImageProcessorFunction::HandleAsync',
    });
    expect(fn.Properties.VpcConfig).toBeUndefined();
  });

  test('environment: bucket and region only, no database settings', () => {
    const variables = processor(dev.media, 'dev').Properties.Environment.Variables;
    expect(Object.keys(variables).sort()).toEqual(['Media__BucketName', 'Media__Region']);
    expect(JSON.stringify(variables.Media__BucketName)).toContain('MediaBucket');
    expect(variables.Media__Region).toBe('eu-central-1');
  });

  test('IAM: uploads/* read+delete, media/* write+read, ListBucket on the bucket; nothing else', () => {
    const all = statements(dev.media);
    const s3 = all.filter((s) => JSON.stringify(s.Action).includes('s3:'));
    const bySid = (sid: string): any => s3.find((s) => s.Sid === sid);

    expect([...bySid('ReadAndRemoveUploads').Action].sort()).toEqual(['s3:DeleteObject', 's3:GetObject']);
    expect(JSON.stringify(bySid('ReadAndRemoveUploads').Resource)).toContain('/uploads/*');
    expect([...bySid('WriteAndReadRenditions').Action].sort()).toEqual(['s3:GetObject', 's3:PutObject']);
    expect(JSON.stringify(bySid('WriteAndReadRenditions').Resource)).toContain('/media/*');
    expect(bySid('ListMediaBucket').Action).toBe('s3:ListBucket');
    // The bucket itself (not objects), without a condition: see the comment in MediaStack.
    expect(bySid('ListMediaBucket').Resource).toEqual({ 'Fn::GetAtt': [expect.stringMatching(/^MediaBucket/), 'Arn'] });
    expect(bySid('ListMediaBucket').Condition).toBeUndefined();
    expect(s3).toHaveLength(3);

    // No VPC, no database, no secrets, no wildcard resource or action.
    for (const statement of all) {
      const actions: string[] = Array.isArray(statement.Action) ? statement.Action : [statement.Action];
      expect(actions.some((action) => action.includes('*'))).toBe(false);
      expect(actions.some((action) => /^(ec2|rds-db|secretsmanager):/.test(action))).toBe(false);
      expect(statement.Resource).not.toBe('*');
    }
  });

  test('S3 notification: every ObjectCreated event under uploads/ invokes the processor', () => {
    const { resource } = onlyResource(dev.media, 'Custom::S3BucketNotifications');
    const configurations = resource.Properties.NotificationConfiguration.LambdaFunctionConfigurations;
    expect(configurations).toHaveLength(1);
    expect(configurations[0].Events).toEqual(['s3:ObjectCreated:*']);
    expect(configurations[0].Filter).toEqual({ Key: { FilterRules: [{ Name: 'prefix', Value: 'uploads/' }] } });
    expect(JSON.stringify(configurations[0].LambdaFunctionArn)).toContain('ImageProcessorFunction');

    // Only S3, only from this bucket and account, may invoke it.
    dev.media.hasResourceProperties('AWS::Lambda::Permission', {
      Action: 'lambda:InvokeFunction',
      Principal: 's3.amazonaws.com',
      SourceAccount: { Ref: 'AWS::AccountId' },
      SourceArn: { 'Fn::GetAtt': [Match.stringLikeRegexp('^MediaBucket'), 'Arn'] },
      FunctionName: { 'Fn::GetAtt': [Match.stringLikeRegexp('^ImageProcessorFunction'), 'Arn'] },
    });
  });

  test('failed events go to an encrypted, TLS-only SQS dead-letter queue after 2 retries', () => {
    dev.media.hasResourceProperties('AWS::SQS::Queue', {
      QueueName: 'zladovce-dev-image-processor-dlq',
      MessageRetentionPeriod: 14 * 24 * 3600,
      SqsManagedSseEnabled: true,
    });
    dev.media.hasResourceProperties('AWS::SQS::QueuePolicy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({ Effect: 'Deny', Condition: { Bool: { 'aws:SecureTransport': 'false' } } }),
        ]),
      },
    });
    const { resource } = onlyResource(dev.media, 'AWS::Lambda::EventInvokeConfig');
    expect(resource.Properties.MaximumRetryAttempts).toBe(2);
    expect(JSON.stringify(resource.Properties.DestinationConfig.OnFailure.Destination)).toContain('ImageProcessorDeadLetterQueue');
    expect(JSON.stringify(resource.Properties.FunctionName)).toContain('ImageProcessorFunction');
  });

  test('log retention per environment; the media stack has no cross-stack imports', () => {
    dev.media.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/zladovce-dev-image-processor',
      RetentionInDays: 30,
    });
    prod.media.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/zladovce-prod-image-processor',
      RetentionInDays: 90,
    });
    expect(JSON.stringify(dev.media.toJSON())).not.toContain('Fn::ImportValue');
    dev.media.hasOutput('ImageProcessorFunctionName', {});
    dev.media.hasOutput('ImageProcessorDeadLetterQueueUrl', {});
  });
});

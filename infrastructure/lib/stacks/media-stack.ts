import { CfnOutput, Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as cloudfront from 'aws-cdk-lib/aws-cloudfront';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { Construct } from 'constructs';
import { EnvironmentConfig, MEDIA_PREFIX, UPLOADS_PREFIX } from '../config';

export interface MediaStackProps extends StackProps {
  readonly config: EnvironmentConfig;
}

/**
 * S3 origin that uses an Origin Access Control but, unlike the stock
 * `S3BucketOrigin.withOriginAccessControl`, does NOT grant CloudFront read access
 * to the whole bucket. The bucket policy is written by `MediaStack` and limited
 * to the `media/` prefix.
 */
class MediaBucketOrigin extends cloudfront.OriginBase {
  constructor(bucket: s3.IBucket, originAccessControl: cloudfront.IOriginAccessControl) {
    super(bucket.bucketRegionalDomainName, {
      originAccessControlId: originAccessControl.originAccessControlId,
    });
  }

  // An S3 origin with OAC must render an S3OriginConfig with an empty origin access identity.
  protected renderS3OriginConfig(): cloudfront.CfnDistribution.S3OriginConfigProperty | undefined {
    return { originAccessIdentity: '' };
  }
}

/**
 * Private media bucket + CloudFront distribution.
 *
 *   uploads/  - raw admin uploads through pre-signed POST; expire after 1 day; never served
 *   media/    - processed renditions; the only prefix CloudFront can read
 *
 * Extension point (later phase): the image processor Lambda subscribes to
 * `uploads/` object-created events on `this.bucket`.
 */
export class MediaStack extends Stack {
  public readonly bucket: s3.Bucket;
  public readonly distribution: cloudfront.Distribution;
  /** Public base URL of media, without trailing slash. Object URL = `${mediaBaseUrl}/media/...`. */
  public readonly mediaBaseUrl: string;

  constructor(scope: Construct, id: string, props: MediaStackProps) {
    super(scope, id, props);
    const { config } = props;
    const media = config.media;

    this.bucket = new s3.Bucket(this, 'MediaBucket', {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      versioned: true,
      objectOwnership: s3.ObjectOwnership.BUCKET_OWNER_ENFORCED,
      removalPolicy: media.retainOnDelete ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY,
      autoDeleteObjects: !media.retainOnDelete && media.autoDeleteObjects,
      lifecycleRules: [
        {
          id: 'ExpireLeftoverUploads',
          prefix: UPLOADS_PREFIX,
          expiration: Duration.days(media.uploadsExpirationDays),
          abortIncompleteMultipartUploadAfter: Duration.days(1),
        },
        {
          id: 'ExpireNoncurrentVersions',
          noncurrentVersionExpiration: Duration.days(media.noncurrentVersionExpirationDays),
          expiredObjectDeleteMarker: true,
        },
      ],
      cors: [
        {
          // Browser uploads with a pre-signed POST/PUT - admin application only.
          allowedMethods: [s3.HttpMethods.PUT, s3.HttpMethods.POST],
          allowedOrigins: config.cors.adminOrigins,
          allowedHeaders: ['*'],
          exposedHeaders: ['ETag'],
          maxAge: 3000,
        },
      ],
    });

    const originAccessControl = new cloudfront.S3OriginAccessControl(this, 'MediaOac', {
      description: `Zladovce ${config.envName} media bucket`,
      signing: cloudfront.Signing.SIGV4_ALWAYS,
    });
    const origin = new MediaBucketOrigin(this.bucket, originAccessControl);

    // Processed renditions are immutable (new content = new key), so cache for a year.
    const mediaCachePolicy = new cloudfront.CachePolicy(this, 'MediaCachePolicy', {
      comment: `Zladovce ${config.envName}: immutable media, cached for one year`,
      minTtl: Duration.days(1),
      defaultTtl: Duration.days(365),
      maxTtl: Duration.days(365),
      cookieBehavior: cloudfront.CacheCookieBehavior.none(),
      headerBehavior: cloudfront.CacheHeaderBehavior.none(),
      queryStringBehavior: cloudfront.CacheQueryStringBehavior.none(),
      enableAcceptEncodingGzip: true,
      enableAcceptEncodingBrotli: true,
    });

    const mediaResponseHeaders = new cloudfront.ResponseHeadersPolicy(this, 'MediaResponseHeaders', {
      comment: `Zladovce ${config.envName}: media cache and security headers`,
      customHeadersBehavior: {
        customHeaders: [
          { header: 'Cache-Control', value: 'public, max-age=31536000, immutable', override: false },
        ],
      },
      securityHeadersBehavior: {
        contentTypeOptions: { override: true },
        strictTransportSecurity: {
          accessControlMaxAge: Duration.days(365),
          includeSubdomains: true,
          override: true,
        },
      },
    });

    // Everything that is not media/* is answered at the edge and never reaches S3.
    const rejectFunction = new cloudfront.Function(this, 'RejectNonMediaFunction', {
      comment: 'Zladovce: 404 for every path outside media/',
      runtime: cloudfront.FunctionRuntime.JS_2_0,
      code: cloudfront.FunctionCode.fromInline(
        [
          'function handler(event) {',
          "  return { statusCode: 404, statusDescription: 'Not Found' };",
          '}',
        ].join('\n'),
      ),
    });

    const customDomain = config.domains.media;

    this.distribution = new cloudfront.Distribution(this, 'MediaDistribution', {
      comment: `Zladovce ${config.envName} media`,
      defaultBehavior: {
        origin,
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        allowedMethods: cloudfront.AllowedMethods.ALLOW_GET_HEAD,
        cachePolicy: cloudfront.CachePolicy.CACHING_DISABLED,
        functionAssociations: [
          { function: rejectFunction, eventType: cloudfront.FunctionEventType.VIEWER_REQUEST },
        ],
      },
      additionalBehaviors: {
        [`${MEDIA_PREFIX}*`]: {
          origin,
          viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
          allowedMethods: cloudfront.AllowedMethods.ALLOW_GET_HEAD,
          cachePolicy: mediaCachePolicy,
          responseHeadersPolicy: mediaResponseHeaders,
          compress: true,
        },
      },
      priceClass: cloudfront.PriceClass.PRICE_CLASS_100,
      httpVersion: cloudfront.HttpVersion.HTTP2_AND_3,
      ...(customDomain
        ? {
            domainNames: [customDomain.domainName],
            minimumProtocolVersion: cloudfront.SecurityPolicyProtocol.TLS_V1_2_2021,
            certificate: acm.Certificate.fromCertificateArn(this, 'MediaCertificate', customDomain.certificateArn),
          }
        : {}),
    });

    // CloudFront (this distribution only) may read media/* and nothing else.
    this.bucket.addToResourcePolicy(
      new iam.PolicyStatement({
        sid: 'AllowCloudFrontReadMediaOnly',
        effect: iam.Effect.ALLOW,
        principals: [new iam.ServicePrincipal('cloudfront.amazonaws.com')],
        actions: ['s3:GetObject'],
        resources: [this.bucket.arnForObjects(`${MEDIA_PREFIX}*`)],
        conditions: {
          StringEquals: { 'AWS:SourceArn': this.distribution.distributionArn },
        },
      }),
    );

    this.mediaBaseUrl = `https://${customDomain?.domainName ?? this.distribution.distributionDomainName}`;

    new CfnOutput(this, 'MediaBucketName', { value: this.bucket.bucketName, description: 'Private media bucket' });
    new CfnOutput(this, 'MediaDistributionDomainName', {
      value: this.distribution.distributionDomainName,
      description: 'CloudFront domain serving media/*',
    });
    new CfnOutput(this, 'MediaDistributionId', { value: this.distribution.distributionId });
    new CfnOutput(this, 'MediaBaseUrl', { value: this.mediaBaseUrl });
  }
}

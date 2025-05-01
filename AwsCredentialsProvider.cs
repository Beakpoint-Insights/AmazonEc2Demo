using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;

namespace AmazonEc2Demo
{
    /// <summary>
    /// Provides AWS credentials and region information for use with AWS SDK clients.
    /// </summary>
    /// <remarks>
    /// This provider attempts to resolve credentials and region settings from the following sources:
    /// <list type="bullet">
    ///     <item>Application configuration (appsettings.json)</item>
    ///     <item>Environment variables</item>
    ///     <item>AWS SDK fallback mechanisms (shared credentials file, instance metadata, etc.)</item>
    /// </list>
    /// </remarks>
    public class AwsCredentialsProvider
    {
        private readonly IConfiguration _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="AwsCredentialsProvider"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration containing optional AWS credential and region settings.</param>
        public AwsCredentialsProvider(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Resolves AWS credentials.
        /// </summary>
        /// <remarks>
        /// If <c>AWS:Credentials:AccessKeyId</c> and <c>AWS:Credentials:SecretAccessKey</c> are specified in the configuration,
        /// they will be used directly. If a <c>AWS:Credentials:SessionToken</c> is also specified, 
        /// <see cref="SessionAWSCredentials"/> will be used. 
        /// Otherwise, the AWS SDK's <see cref="FallbackCredentialsFactory"/> will be used to locate credentials.
        /// </remarks>
        /// <returns>An instance of <see cref="AWSCredentials"/> for authenticating AWS SDK clients.</returns>
        public AWSCredentials GetCredentials()
        {
            var accessKey = _configuration["AWS:Credentials:AccessKeyId"];
            var secretKey = _configuration["AWS:Credentials:SecretAccessKey"];
            var sessionToken = _configuration["AWS:Credentials:SessionToken"];

            if (!string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey))
            {
                if (!string.IsNullOrWhiteSpace(sessionToken))
                    return new SessionAWSCredentials(accessKey, secretKey, sessionToken);
            }

            return FallbackCredentialsFactory.GetCredentials();
        }

        /// <summary>
        /// Resolves the AWS region.
        /// </summary>
        /// <remarks>
        /// The region is resolved in the following order:
        /// <list type="bullet">
        ///     <item>Configuration key <c>AWS:Region</c></item>
        ///     <item>Environment variable <c>AWS_REGION</c></item>
        ///     <item>AWS SDK fallback mechanism</item>
        /// </list>
        /// </remarks>
        /// <returns>The resolved <see cref="RegionEndpoint"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the region cannot be resolved from any source.</exception>
        public RegionEndpoint GetRegion()
        {
            var regionName = _configuration["AWS:Region"]
                             ?? Environment.GetEnvironmentVariable("AWS_REGION")
                             ?? FallbackRegionFactory.GetRegionEndpoint()?.SystemName;

            if (string.IsNullOrWhiteSpace(regionName))
                throw new InvalidOperationException("AWS Region must be configured via appsettings or AWS_REGION environment variable.");

            return RegionEndpoint.GetBySystemName(regionName);
        }

    }
}

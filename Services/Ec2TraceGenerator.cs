// An example program that shows how to send traces to Beakpoint Insights
// which contain the metadata necessary to calculate the cost of a query against
// an RDS instance.
// 
// Copyright (C) 2025 Beakpoint Insights, Inc.
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.

using System.Diagnostics;
using Amazon.EC2;
using Amazon.EC2.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
//using Npgsql;

namespace AmazonEc2Demo.Services;

/// <summary>
/// Service responsible for generating an OpenTelemetry trace of a PostgreSQL query,
/// enriched with Amazon RDS instance metadata.
/// </summary>
/// <param name="awsCredentialsProvider">
/// Provider responsible for resolving AWS credentials and region from configuration,
/// environment variables, or fallback mechanisms.
/// Used to authenticate requests to the Amazon RDS service.
/// </param>
/// <param name="activitySource">ActivitySource used for creating spans.</param>
/// <param name="logger">Logger instance for logging information and errors.</param>
public class Ec2TraceGenerator(
AwsCredentialsProvider awsCredentialsProvider,
ActivitySource activitySource,
    ILogger<Ec2TraceGenerator> logger,
    IConfiguration configuration)
{
    /// <summary>
    /// Generates a single trace representing a PostgreSQL query execution enriched with Amazon RDS metadata.
    /// </summary>
    /// <remarks>
    /// This method:
    /// <list type="bullet">
    ///     <item>Starts an OpenTelemetry activity named "RDS::Query".</item>
    ///     <item>Collects RDS instance metadata via AWS SDK.</item>
    ///     <item>Performs a sample SQL query (<c>SELECT NOW()</c>) to the RDS PostgreSQL instance.</item>
    ///     <item>Exports the trace via the configured OpenTelemetry exporter.</item>
    /// </list>
    /// </remarks>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task GenerateTraceWithMetadataAsync()
    {
        using var activity = activitySource.StartActivity("RDS::Query", ActivityKind.Client);

        if (activity == null)
        {
            logger.LogWarning("❌ Activity is null. Skipping trace generation.");
            return;
        }

        const string query = "SELECT NOW()";

        var ec2Metadata = await GetEc2MetadataAsync();

        activity.SetTag("aws.ec2.instance.id", ec2Metadata.InstanceIdentifier);
        activity.SetTag("aws.ec2.instance.type", ec2Metadata.InstanceType);
        activity.SetTag("aws.region", ec2Metadata.Region);
        activity.SetTag("aws.ec2.operating_system", ec2Metadata.OperatingSystem);
        activity.SetTag("aws.ec2.license.model", ec2Metadata.LicenseModel);
        activity.SetTag("aws.ec2.tenancy", ec2Metadata.Tenancy);

        activity.SetTag("db.system", "postgresql");
        activity.SetTag("db.operation", "query");
        activity.SetTag("db.statement", query);

        //try
        //{
        //    var connStr = configuration["ConnectionStrings:RDS"] ?? throw new InvalidOperationException("ConnectionStrings:RDS is not set");
        //
        //    await using var connection = new NpgsqlConnection(connStr);
        //    await connection.OpenAsync();
        //    await using var command = new NpgsqlCommand(query, connection);
        //    var result = await command.ExecuteScalarAsync();
        //    activity.SetTag("db.success", true);
        //    logger.LogInformation("[REAL] Query result: {Result}", result);
        //}
        //catch (Exception ex)
        //{
        //    activity.SetTag("db.success", false);
        //    activity.SetTag("db.error", ex.Message);
        //    logger.LogError(ex, "❌ Query failed");
        //}
    }

    /// <summary>
    /// Retrieves metadata information about an Amazon RDS instance for trace enrichment.
    /// </summary>
    /// <remarks>
    /// This method performs the following:
    /// <list type="bullet">
    ///     <item>Uses AWS credentials and region from the credentials provider.</item>
    ///     <item>Queries Amazon RDS for a specific instance if <c>RDS:InstanceId</c> is configured, otherwise retrieves the first available instance.</item>
    ///     <item>Returns essential metadata required for trace enrichment (e.g., engine, instance class, storage type).</item>
    ///     <item>If <c>RDS:InstanceId</c> is specified, queries metadata for the specified RDS instance.</item>
    /// </list>
    /// </remarks>
    /// <returns>A populated <see cref="RdsMetadata"/> object containing RDS instance details.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if no RDS instances are found in the account or the specified instance does not exist.
    /// </exception>
    private async Task<Ec2Metadata> GetEc2MetadataAsync()
    {
        var credentials = awsCredentialsProvider.GetCredentials();
        var region = awsCredentialsProvider.GetRegion();

        var client = new AmazonEC2Client(credentials, new AmazonEC2Config
        {
            RegionEndpoint = region
        });


        var instanceId = configuration["RDS:InstanceId"];

        var request = string.IsNullOrWhiteSpace(instanceId)
            ? new DescribeInstancesRequest()
            : new DescribeInstancesRequest { InstanceIds = new System.Collections.Generic.List<string> { instanceId } };

        var response = await client.DescribeInstancesAsync(request);

        if (response.Reservations.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(instanceId)
                ? "No RDS instances found in the account"
                : $"RDS instance with id '{instanceId}' not found";

            throw new InvalidOperationException(message);
        }

        var instance = response.Reservations.First().Instances.First();

        //if (!string.IsNullOrEmpty(instance.ImageId))
        //{
        //    var amiRequest = new DescribeImagesRequest
        //    {
        //        ImageIds = new System.Collections.Generic.List<string> { instance.ImageId }
        //    };
        //    var amiResponse = await client.DescribeImagesAsync(amiRequest);
        //    if (amiResponse.Images.Count > 0)
        //    {
        //        var image = amiResponse.Images[0];
        //        var platformDetails = image.PlatformDetails?.ToLower() ?? "";
        //        // Refine OperatingSystem
        //        if (platformDetails.Contains("windows"))
        //        {
        //            operatingSystem = "Windows";
        //            licenseModel = "License Included";
        //        }
        //        else if (platformDetails.Contains("red hat"))
        //        {
        //            operatingSystem = "RHEL";
        //            licenseModel = "License Included"; // RHEL includes licensing fees
        //        }
        //        else if (platformDetails.Contains("suse"))
        //        {
        //            operatingSystem = "SUSE";
        //            licenseModel = "License Included";
        //        }
        //        else if (platformDetails.Contains("linux/unix"))
        //        {
        //            operatingSystem = "Linux";
        //            licenseModel = "No License required";
        //        }
        //        // Check for Bring Your Own License (BYOL) via UsageOperation or tags
        //        if (image.UsageOperation?.Contains("BYOL", StringComparison.OrdinalIgnoreCase) == true)
        //        {
        //            licenseModel = "Bring Your Own License";
        //        }
        //    }
        //}

        var metadata = new Ec2Metadata
        {
            InstanceIdentifier = instance.InstanceId,
            InstanceType = instance.InstanceType.Value,
            Region = client.Config.RegionEndpoint.SystemName,
            OperatingSystem = instance.PlatformDetails,
            //LicenseModel = instance.LicenseModel,
            Tenancy = instance.Placement.Tenancy,
        };

        Console.WriteLine(metadata.InstanceIdentifier);
        Console.WriteLine(metadata.InstanceType);
        Console.WriteLine(metadata.Region);
        Console.WriteLine(metadata.OperatingSystem);
        Console.WriteLine(metadata.LicenseModel);
        Console.WriteLine(metadata.Tenancy);

        return metadata;
    }

    /// <summary>
    /// Represents the metadata of an Amazon RDS instance.
    /// </summary>
    private class Ec2Metadata
    {
        public string InstanceIdentifier { get; set; } = null!;
        public string InstanceType { get; set; } = null!;
        public string Region { get; set; } = null!;
        public string OperatingSystem { get; set; } = null!;
        public string LicenseModel { get; set; } = null!;
        public string Tenancy { get; set; } = null!;
    }
}
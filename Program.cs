// Beakpoint Insights, Inc. licenses this file to you under the MIT license.

using System.Net;
using System.Reflection;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.Util;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AmazonEc2Demo;

/// <summary>
///     The Program class serves as the entry point for the AmazonEc2Demo application.
///     It configures and initializes the application, including setting up OpenTelemetry
///     for tracing, configuring application settings, and defining request handling routes.
/// </summary>
public static class Program
{
    /// <summary>
    ///     Serves as the entry point for the AmazonEc2Demo application, setting up the application configuration,
    ///     initializing OpenTelemetry tracing, and starting the web application.
    /// </summary>
    /// <param name="args">An array of command-line arguments passed to the application.</param>
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // Create a path to configuration files
        var basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var projectPath = Path.GetFullPath(Path.Combine(basePath, Path.Combine("..", "..", "..")));

        // Configuration setup
        builder.Configuration
            .SetBasePath(projectPath)
            .AddJsonFile("appsettings.json", false, true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true)
            .AddEnvironmentVariables();

        // Get Telemetry receiver address and API key
        var apiKey = builder.Configuration["Beakpoint:Otel:ApiKey"];
        var url = builder.Configuration["Beakpoint:Otel:Url"] ??
                  throw new InvalidOperationException("Beakpoint Otel Url is not configured");

        // Get instance metadata
        var attributes = GetAttributes(builder.Configuration).GetAwaiter().GetResult();

        // Add OpenTelemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracingBuilder => tracingBuilder
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddAttributes(attributes))
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.EnrichWithHttpRequest = (activity, _) =>
                    {
                        foreach (var attr in attributes) activity.SetTag(attr.Key, attr.Value);
                    };
                })
                .AddHttpClientInstrumentation(options =>
                {
                    options.EnrichWithHttpRequestMessage = (activity, _) =>
                    {
                        foreach (var attr in attributes) activity.SetTag(attr.Key, attr.Value);
                    };
                })
                .AddAWSInstrumentation()
                .AddOtlpExporter(opts =>
                {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.ExportProcessorType = ExportProcessorType.Simple;
                    opts.Endpoint = new Uri(url);
                    opts.Headers = $"x-bkpt-key={apiKey}";
                }));

        builder.WebHost.ConfigureKestrel(serverOptions => { serverOptions.Listen(IPAddress.Parse("0.0.0.0"), 5227); });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");

        app.MapGet("/", () => "Hello World!");

        app.Run();
    }

    /// <summary>
    ///     Retrieves a dictionary of OpenTelemetry resource attributes that identify an EC2 instance.
    ///     These attributes follow OpenTelemetry semantic conventions where available, with custom
    ///     AWS-specific attributes for fields that have no semantic convention equivalent.
    ///     Required attributes (must always be present):
    ///     - cloud.platform: Always "aws_ec2" for EC2 instances
    ///     - host.id: The EC2 instance ID
    ///     - host.type: The EC2 instance type (e.g., "c6i.large")
    ///     - cloud.region: The AWS region (e.g., "us-east-1")
    ///     - os.type: Either "linux" or "windows"
    ///     Optional attributes (included when relevant):
    ///     - aws.ec2.platform_details: Only included for non-default platforms such as RHEL, SUSE, SQL Server, etc.
    ///     - aws.ec2.license_model: Included with comment explaining default behavior
    ///     - aws.ec2.tenancy: Included with comment explaining default behavior
    ///     - aws.ec2.instance_lifecycle: Only if instance is a spot instance
    ///     - aws.ec2.capacity_reservation_id: Only if instance uses a capacity reservation
    ///     - aws.ec2.capacity_reservation_preference: Only if capacity reservation is used
    ///     - aws.ec2.fleet_id: Only if instance belongs to a fleet
    /// </summary>
    /// <param name="configuration">The configuration object, used to retrieve AWS credentials.</param>
    /// <returns>A dictionary of attributes that identify the instance for cost attribution.</returns>
    private static async Task<Dictionary<string, object>> GetAttributes(IConfiguration configuration)
    {
        // Get AWS credentials
        var accessKey = configuration["AWS:Credentials:AccessKeyId"];
        var secretKey = configuration["AWS:Credentials:SecretAccessKey"];
        var sessionToken = configuration["AWS:Credentials:SessionToken"];

        var regionName = EC2InstanceMetadata.AvailabilityZone[..^1];

        var credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);

        var region = RegionEndpoint.GetBySystemName(regionName);

        // Initialize EC2 client
        var client = new AmazonEC2Client(credentials, new AmazonEC2Config
        {
            RegionEndpoint = region
        });

        // Fetch instance details from AWS
        var instance = await DescribeInstancesApiCall(client, EC2InstanceMetadata.InstanceId);

        // Fetch optional purchase option details
        string? capacityReservationPreference = null;
        if (!string.IsNullOrEmpty(instance.CapacityReservationId))
            capacityReservationPreference = await DescribeCapacityReservationsApiCall(client, instance);
        var fleetId = await GetFleetIdApiCall(client, instance);

        // Build the attributes dictionary using OpenTelemetry semantic conventions
        var resultingAttributes = new Dictionary<string, object>
        {
            // Required: Identifies this as an EC2 instance
            ["cloud.platform"] = "aws_ec2",

            // Required: Instance identification (semantic conventions)
            ["host.id"] = instance.InstanceId,
            ["host.type"] = instance.InstanceType.Value,
            ["cloud.region"] = regionName,

            // Required: The operating system type derived from platform details
            ["os.type"] = GetOsType(instance.PlatformDetails),

            // Optional but included here to demonstrate: license model
            // If omitted, the pricing system defaults to "No License required"
            ["aws.ec2.license_model"] = instance.Licenses is null || instance.Licenses.Count == 0
                ? "No License required"
                : "Bring your own license",

            // Optional but included here to demonstrate: tenancy
            // If omitted, the pricing system defaults to "shared"
            // Note: AWS returns "default" which Beakpoint Insights' pricing system treats as equivalent to "shared"
            ["aws.ec2.tenancy"] = instance.Placement.Tenancy.Value
        };

        // Only include platform_details for non-default platforms
        // Default platforms ("Linux/UNIX" and "Windows") don't need this attribute
        if (ShouldIncludePlatformDetails(instance.PlatformDetails))
            resultingAttributes["aws.ec2.platform_details"] = instance.PlatformDetails;

        // Purchase option determining attributes (only included when applicable)
        if (instance.InstanceLifecycle is not null)
            resultingAttributes.Add("aws.ec2.instance_lifecycle", instance.InstanceLifecycle.Value);
        if (instance.CapacityReservationId is not null)
            resultingAttributes.Add("aws.ec2.capacity_reservation_id", instance.CapacityReservationId);
        if (capacityReservationPreference is not null)
            resultingAttributes.Add("aws.ec2.capacity_reservation_preference", capacityReservationPreference);
        if (fleetId is not null)
            resultingAttributes.Add("aws.ec2.fleet_id", fleetId);

        return resultingAttributes;
    }

    /// <summary>
    ///     Makes a DescribeInstances API call using the provided AmazonEC2Client and EC2 instance id.
    ///     The request is constructed based on the presence of the EC2InstanceMetadata.InstanceId value.
    ///     If the value is not present, the request is made with no filters.
    ///     If the value is present, the request is made with a filter on the InstanceId.
    ///     The response is then parsed to return the first EC2 instance.
    ///     If no instances are found, or if the specified instance id is not found, an InvalidOperationException is thrown.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="instanceId">The EC2 instance id to filter by.</param>
    /// <returns>The first EC2 instance found in the response.</returns>
    /// <exception cref="InvalidOperationException">
    ///     Thrown if no instances are found, or if the specified instance id is not
    ///     found.
    /// </exception>
    private static async Task<Instance> DescribeInstancesApiCall(AmazonEC2Client client, string instanceId)
    {
        var request = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
            ? new DescribeInstancesRequest()
            : new DescribeInstancesRequest { InstanceIds = [instanceId] };

        var response = await client.DescribeInstancesAsync(request);

        if (response.Reservations.Count != 0)
            return response.Reservations.First().Instances.First();

        var message = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
            ? "No EC2 instances found in the account"
            : $"EC2 instance with id '{EC2InstanceMetadata.InstanceId}' not found";

        throw new InvalidOperationException(message);
    }

    /// <summary>
    ///     Derives the `os.type` attribute value from AWS platform details.
    ///     Returns "windows" if the platform details contain "Windows" (case-insensitive),
    ///     otherwise returns "linux".
    /// </summary>
    /// <param name="platformDetails">
    ///     The platform details string from AWS (e.g., "Linux/UNIX", "Windows", "Red Hat Enterprise
    ///     Linux").
    /// </param>
    /// <returns>"windows" or "linux"</returns>
    private static string GetOsType(string platformDetails)
    {
        return platformDetails.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            ? "windows"
            : "linux";
    }

    /// <summary>
    ///     Determines whether the aws.ec2.platform_details attribute should be included.
    ///     Returns false for basic platforms ("Linux/UNIX" or "Windows") where the default is enough.
    ///     Returns true for all other platforms (RHEL, SUSE, Ubuntu Pro, SQL Server variants, etc.)
    ///     where explicit platform details are needed for accurate pricing.
    /// </summary>
    /// <param name="platformDetails">The platform details string from AWS.</param>
    /// <returns>True if platform_details should be included in the attributes, false otherwise.</returns>
    /// <see href="https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/billing-info-fields.html">
    ///     Read more about the different
    ///     platform details that AWS may set.
    /// </see>
    private static bool ShouldIncludePlatformDetails(string platformDetails)
    {
        return platformDetails != "Linux/UNIX" && platformDetails != "Windows";
    }

    /// <summary>
    ///     Makes a DescribeCapacityReservations API call using the provided AmazonEC2Client and EC2 instance.
    ///     It retrieves the capacity reservation preference based on the instance's CapacityReservationId.
    ///     If a capacity reservation is found and has available instances, the preference is set to "open".
    ///     Otherwise, the preference defaults to null.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="instance">The EC2 instance whose capacity reservation preference is to be determined.</param>
    /// <returns>The capacity reservation preference as a string ("open" or "none").</returns>
    private static async Task<string?> DescribeCapacityReservationsApiCall(AmazonEC2Client client, Instance instance)
    {
        string? capacityReservationPreference = null;

        if (string.IsNullOrEmpty(instance.CapacityReservationId)) return capacityReservationPreference;

        var capacityReservationRequest = new DescribeCapacityReservationsRequest
        {
            CapacityReservationIds = [instance.CapacityReservationId]
        };

        var capacityReservationResponse = await client.DescribeCapacityReservationsAsync(capacityReservationRequest);

        if (capacityReservationResponse.CapacityReservations.Count > 0)
            // Assume preference based on reservation usage (simplified; "open" if available, "none" if not)
            capacityReservationPreference =
                capacityReservationResponse.CapacityReservations[0].AvailableInstanceCount > 0
                    ? "open"
                    : null;

        return capacityReservationPreference;
    }

    /// <summary>
    ///     Makes a DescribeFleets API call using the provided AmazonEC2Client and EC2 instance.
    ///     It retrieves the fleet id associated with the instance, if any.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="instance">The EC2 instance whose fleet id is to be determined.</param>
    /// <returns>The fleet id associated with the instance, or null if no fleet is associated.</returns>
    private static async Task<string?> GetFleetIdApiCall(AmazonEC2Client client, Instance instance)
    {
        string? fleetId = null;

        var describeFleetsRequest = new DescribeFleetsRequest();
        var fleetResponse = await client.DescribeFleetsAsync(describeFleetsRequest);
        Console.WriteLine($"Fleet response: {fleetResponse}");
        Console.WriteLine($"Fleets found: {fleetResponse.Fleets is not null}");
        Console.WriteLine($"Fleets found: {fleetResponse.Fleets}");

        if (fleetResponse.Fleets == null || fleetResponse.Fleets.Count == 0)
            return fleetId;

        Console.WriteLine(
            $"Fleets found: {fleetResponse.Fleets.Count}\nFirst fleet ID: {fleetResponse.Fleets[0].FleetId}");

        foreach (var fleet in fleetResponse.Fleets)
        {
            var fleetInstancesRequest = new DescribeFleetInstancesRequest
            {
                FleetId = fleet.FleetId
            };

            var fleetInstancesResponse = await client.DescribeFleetInstancesAsync(fleetInstancesRequest);

            if (fleetInstancesResponse.ActiveInstances.All(i => i.InstanceId != instance.InstanceId))
                continue;

            fleetId = fleet.FleetId;
        }

        return fleetId;
    }
}
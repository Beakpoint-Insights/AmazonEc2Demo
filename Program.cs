// Beakpoint Insights, Inc. licenses this file to you under the MIT license.

using System.Reflection;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.Util;
using Microsoft.Extensions.Caching.Memory;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AmazonEc2Demo;

public static class Program
{
    public static void Main(string[] args) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        // Create a path to configuration files
        var basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var projectPath = Path.GetFullPath(Path.Combine(basePath, Path.Combine("..", "..", "..")));
        
        // Configuration setup
        builder.Configuration
            .SetBasePath(projectPath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
            .AddEnvironmentVariables();

        builder.Services.AddMemoryCache();

        // Resolve IMemoryCache from the service provider
        #pragma warning disable ASP0000
        ServiceProvider serviceProvider = builder.Services.BuildServiceProvider();
        IMemoryCache memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();

        // Get Telemetry receiver address and API key
        var apiKey = builder.Configuration["Beakpoint:Otel:ApiKey"];
        var url = builder.Configuration["Beakpoint:Otel:Url"] ?? throw new InvalidOperationException("Beakpoint Otel Url is not configured");

        // Get instance metadata
        var attributes = GetAttributes(builder.Configuration, memoryCache).GetAwaiter().GetResult();

        // Add OpenTelemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracingBuilder => tracingBuilder
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddAttributes(attributes))
                .AddAspNetCoreInstrumentation(options => {
                    options.EnrichWithHttpRequest = (activity, request) => {
                        foreach(var attr in attributes) {
                            activity.SetTag(attr.Key, attr.Value);
                        }
                    };
                })
                .AddHttpClientInstrumentation(options =>{
                    options.EnrichWithHttpRequestMessage = (activity, request) => {
                        foreach(var attr in attributes) {
                            activity.SetTag(attr.Key, attr.Value);
                        }
                    };
                })
                .AddAWSInstrumentation()
                .AddOtlpExporter(opts => {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.ExportProcessorType = ExportProcessorType.Simple;
                    opts.Endpoint = new Uri(url);
                    opts.Headers = $"x-bkpt-key={apiKey}";
                }));

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(System.Net.IPAddress.Parse("0.0.0.0"), 5227);
        });

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment()) {
            app.UseExceptionHandler("/Error");
        }

        app.MapGet("/", () => "Hello World!");

        app.Run();
    }

    /// <summary>
    /// Retrieves a dictionary of attributes that identify an EC2 instance,
    /// including its ID, type, region, operating system, tenancy, and lifecycle.
    /// The dictionary is meant to be used as the resource attributes for an OTel
    /// span, so that it can be used to attribute costs to the instance.
    /// </summary>
    /// <param name="configuration">The configuration object, used to retrieve
    /// AWS credentials and other configuration values.</param>
    /// <param name="memoryCache">The memory cache object, used to store the
    /// fleet ID for the instance.</param>
    /// <returns>A dictionary of attributes that identify the instance.</returns>
    private static async Task<Dictionary<string, object>> GetAttributes(IConfiguration configuration, IMemoryCache memoryCache)
    {
        // Get AWS credentials
        var accessKey = configuration["AWS:Credentials:AccessKeyId"];
        var secretKey = configuration["AWS:Credentials:SecretAccessKey"];
        var sessionToken = configuration["AWS:Credentials:SessionToken"];

        var regionName = EC2InstanceMetadata.AvailabilityZone[..^1];

        SessionAWSCredentials credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);

        RegionEndpoint? region = RegionEndpoint.GetBySystemName(regionName);

        // Initialize EC2 client
        AmazonEC2Client client = new AmazonEC2Client(credentials, new AmazonEC2Config
        {
            RegionEndpoint = region
        });

        // Describe instance request
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId + "_Instance", out Instance? instance) || instance is null)
        {
            instance = await DescribeInstancesApiCall(client, EC2InstanceMetadata.InstanceId);
            memoryCache.Set(EC2InstanceMetadata.InstanceId + "_Instance", instance, new MemoryCacheEntryOptions());
        }

        // Clarify platform details request
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId + "_PlatformDetails", out string? platformDetails) || platformDetails is null)
        {
            platformDetails = await ClarifyPlatformDetailsAsync(client, instance.ImageId, memoryCache, instance.PlatformDetails);
            memoryCache.Set(EC2InstanceMetadata.InstanceId + "_PlatformDetails", platformDetails, new MemoryCacheEntryOptions());
        }

        // Describe capacity reservation request to ger capacity reservation preference
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId + "_CapacityReservationPreference", out string? capacityReservationPreference)
            || string.IsNullOrEmpty(capacityReservationPreference))
        {
            capacityReservationPreference = await DescribeCapacityReservationsApiCall(client, instance);
            memoryCache.Set(EC2InstanceMetadata.InstanceId + "_CapacityReservationPreference", capacityReservationPreference, new MemoryCacheEntryOptions());
        }

        // Describe fleets request to get the fleet id
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId + "_FleetId", out string? fleetId)
            || string.IsNullOrEmpty(fleetId))
        {
            fleetId = await GetFleetIdApiCall(client, instance);
            memoryCache.Set(EC2InstanceMetadata.InstanceId + "_FleetId", fleetId, new MemoryCacheEntryOptions());
        }

        return new Dictionary<string, object>
        {
            ["aws.ec2.instance_id"] = instance.InstanceId,
            ["aws.ec2.instance_type"] = instance.InstanceType.Value,
            ["aws.region"] = regionName,
            ["aws.ec2.platform_details"] = platformDetails,
            ["aws.ec2.license_model"] = instance.Licenses.Count == 0 ? "No License required" : "Bring your own license",
            ["aws.ec2.tenancy"] = instance.Placement.Tenancy.Value,
            ["aws.ec2.instance_lifecycle"] = instance.InstanceLifecycle.Value,
            ["aws.ec2.capacity_reservation_id"] = instance.CapacityReservationId,
            ["aws.ec2.capacity_reservation_preference"] = capacityReservationPreference,
            ["aws.ec2.fleet_id"] = fleetId
        };
    }

    private static async Task<Instance> DescribeInstancesApiCall(AmazonEC2Client client, string instanceId)
    {
        DescribeInstancesRequest request = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
        ? new DescribeInstancesRequest()
        : new DescribeInstancesRequest { InstanceIds = [instanceId] };

        DescribeInstancesResponse? response = await client.DescribeInstancesAsync(request);

        if (response.Reservations.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
                ? "No EC2 instances found in the account"
                : $"EC2 instance with id '{EC2InstanceMetadata.InstanceId}' not found";

            throw new InvalidOperationException(message);
        }

        return response.Reservations.First().Instances.First();
    }

    private static async Task<string> DescribeCapacityReservationsApiCall(AmazonEC2Client client, Instance instance)
    {
        var capacityReservationPreference = "none";

        if (!string.IsNullOrEmpty(instance.CapacityReservationId))
        {
            DescribeCapacityReservationsRequest capacityReservationRequest = new DescribeCapacityReservationsRequest
            {
                CapacityReservationIds = [instance.CapacityReservationId]
            };
            DescribeCapacityReservationsResponse? capacityReservationResponse = await client.DescribeCapacityReservationsAsync(capacityReservationRequest);
            if (capacityReservationResponse.CapacityReservations.Count > 0)
            {
                // Assume preference based on reservation usage (simplified; "open" if available, "none" if not)
                capacityReservationPreference = capacityReservationResponse.CapacityReservations[0].AvailableInstanceCount > 0
                    ? "open"
                    : "none";
            }
        }

        return capacityReservationPreference;
    }

    private static async Task<string> GetFleetIdApiCall(AmazonEC2Client client, Instance instance)
    {
        var fleetId = string.Empty;

        DescribeFleetsRequest describeFleetsRequest = new DescribeFleetsRequest();
        DescribeFleetsResponse? fleetResponse = await client.DescribeFleetsAsync(describeFleetsRequest);

        if (fleetResponse.Fleets != null && fleetResponse.Fleets.Count == 0)
        {
            foreach (FleetData? fleet in fleetResponse.Fleets)
            {
                DescribeFleetInstancesRequest fleetInstancesRequest = new DescribeFleetInstancesRequest
                {
                    FleetId = fleet.FleetId
                };
                DescribeFleetInstancesResponse? fleetInstancesResponse = await client.DescribeFleetInstancesAsync(fleetInstancesRequest);
                if (fleetInstancesResponse.ActiveInstances.Any(i => i.InstanceId == instance.InstanceId))
                {
                    fleetId = fleet.FleetId;
                    break;
                }
            }
        }

        return fleetId;
    }

    private static async Task<string> ClarifyPlatformDetailsAsync(AmazonEC2Client client, string imageId, IMemoryCache memoryCache, string originalPlatformDetails)
    {
        string[] platformDetailsWithoutOs = 
        [
            "SQL Server Standard",
            "SQL Server Enterprise",
            "SQL Server Web"
        ];

        if (platformDetailsWithoutOs.Contains(originalPlatformDetails))
        {
            if (!string.IsNullOrEmpty(imageId))
            {
                DescribeImagesRequest describeImagesRequest = new DescribeImagesRequest
                {
                    ImageIds = [imageId]
                };
                DescribeImagesResponse? describeImagesResponse = await client.DescribeImagesAsync(describeImagesRequest);
                if (describeImagesResponse.Images.Count > 0)
                {
                    Image? image = describeImagesResponse.Images[0];
                    var name = image.Name?.ToLower() ?? "";
                    var description = image.Description?.ToLower() ?? "";
                    var imagePlatformDetails = image.PlatformDetails ?? originalPlatformDetails;

                    if (originalPlatformDetails.StartsWith("SQL Server"))
                    {
                        if (name.Contains("amzn") || description.Contains("amazon linux"))
                            return "Amazon Linux 2 with " + originalPlatformDetails;
                        if (name.Contains("windows") || description.Contains("windows"))
                            return "Windows with " + originalPlatformDetails;
                        if (name.Contains("ubuntu") || description.Contains("ubuntu"))
                            return "Ubuntu with " + originalPlatformDetails;
                        if (name.Contains("rhel") || description.Contains("rhel") || name.Contains("red hat") || description.Contains("red hat"))
                            return "Red Hat Enterprise Linux with " + originalPlatformDetails;
                    }
                    return imagePlatformDetails;
                }
            }
        }
        return originalPlatformDetails;
    }
}

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

public static class Program {
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
    static async Task<Dictionary<string, object>> GetAttributes(IConfiguration configuration, IMemoryCache memoryCache)
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
        if (!memoryCache.TryGetValue($"{EC2InstanceMetadata.InstanceId}_instance", out Instance? instance) || instance is null)
        {
            instance = await DescribeInstancesApiCall(client, EC2InstanceMetadata.InstanceId);
            memoryCache.Set($"{EC2InstanceMetadata.InstanceId}_instance", instance, new MemoryCacheEntryOptions());
        }

        // Clarify platform details request
        if (!memoryCache.TryGetValue($"{EC2InstanceMetadata.InstanceId}_platformDetails", out string? platformDetails) || platformDetails is null)
        {
            platformDetails = await ClarifyPlatformDetailsAsync(client, instance.ImageId, memoryCache, instance.PlatformDetails);
            memoryCache.Set($"{EC2InstanceMetadata.InstanceId}_platformDetails", platformDetails, new MemoryCacheEntryOptions());
        }

        // Describe capacity reservation request to ger capacity reservation preference
        if ((!memoryCache.TryGetValue($"{EC2InstanceMetadata.InstanceId}_capacityReservationPreference", out string? capacityReservationPreference)
            || string.IsNullOrEmpty(capacityReservationPreference))
            && !string.IsNullOrEmpty(instance.CapacityReservationId))
        {
            capacityReservationPreference = await DescribeCapacityReservationsApiCall(client, instance);
            memoryCache.Set($"{EC2InstanceMetadata.InstanceId}_capacityReservationPreference", new MemoryCacheEntryOptions());
        }

        // Describe fleets request to get the fleet id
        if (!memoryCache.TryGetValue($"{EC2InstanceMetadata.InstanceId}_fleetId", out string? fleetId)
            || string.IsNullOrEmpty(fleetId))
        {
            fleetId = await GetFleetIdApiCall(client, instance);
            memoryCache.Set($"{EC2InstanceMetadata.InstanceId}_fleetId", fleetId, new MemoryCacheEntryOptions());
        }

        // Core attributes
        var resultingAttributes = new Dictionary<string, object>
        {
            ["aws.ec2.instance_id"] = instance.InstanceId,
            ["aws.ec2.instance_type"] = instance.InstanceType.Value,
            ["aws.region"] = regionName,
            ["aws.ec2.platform_details"] = platformDetails,
            ["aws.ec2.license_model"] = instance.Licenses is null || instance.Licenses.Count == 0 ? "No License required" : "Bring your own license",
            ["aws.ec2.tenancy"] = instance.Placement.Tenancy.Value
        };

        // Purchase option determining attributes
        if (instance.InstanceLifecycle is not null) resultingAttributes.Add("aws.ec2.instance_lifecycle", instance.InstanceLifecycle.Value);
        if (instance.CapacityReservationId is not null) resultingAttributes.Add("aws.ec2.capacity_reservation_id", instance.CapacityReservationId);
        if (capacityReservationPreference is not null) resultingAttributes.Add("aws.ec2.capacity_reservation_preference", capacityReservationPreference);
        if (fleetId is not null) resultingAttributes.Add("aws.ec2.fleet_id", fleetId);
        
        return resultingAttributes;
    }

    /// <summary>
    /// Makes a DescribeInstances API call using the provided AmazonEC2Client and EC2 instance id.
    /// The request is constructed based on the presence of the EC2InstanceMetadata.InstanceId value.
    /// If the value is not present, the request is made with no filters.
    /// If the value is present, the request is made with a filter on the InstanceId.
    /// The response is then parsed to return the first EC2 instance.
    /// If no instances are found, or if the specified instance id is not found, an InvalidOperationException is thrown.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="instanceId">The EC2 instance id to filter by.</param>
    /// <returns>The first EC2 instance found in the response.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no instances are found, or if the specified instance id is not found.</exception>
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

    /// <summary>
    /// Clarifies the platform details of an EC2 instance by including the underlying OS if the platform details do not already include the OS.
    /// If the platform details do not contain the OS, the function makes a DescribeImages API call to get the OS and then returns a string in the following format: [OS] with [original platform details].
    /// If the platform details do contain the OS, the function returns the original platform details.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="imageId">The EC2 image id to filter by.</param>
    /// <param name="memoryCache">The IMemoryCache to use for caching the response.</param>
    /// <param name="originalPlatformDetails">The original platform details as returned by the DescribeInstances API call.</param>
    /// <returns>The platform details with the OS included if it was not already included.</returns>
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

    /// <summary>
    /// Makes a DescribeCapacityReservations API call using the provided AmazonEC2Client and EC2 instance.
    /// It retrieves the capacity reservation preference based on the instance's CapacityReservationId.
    /// If a capacity reservation is found and has available instances, the preference is set to "open".
    /// Otherwise, the preference defaults to null.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="instance">The EC2 instance whose capacity reservation preference is to be determined.</param>
    /// <returns>The capacity reservation preference as a string ("open" or "none").</returns>
    private static async Task<string?> DescribeCapacityReservationsApiCall(AmazonEC2Client client, Instance instance)
    {
        string? capacityReservationPreference = null;

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
                    : null;
            }
        }

        return capacityReservationPreference;
    }

    /// <summary>
    /// Makes a DescribeFleets API call using the provided AmazonEC2Client and EC2 instance.
    /// It retrieves the fleet id associated with the instance, if any.
    /// </summary>
    /// <param name="client">The AmazonEC2Client to use for the request.</param>
    /// <param name="instance">The EC2 instance whose fleet id is to be determined.</param>
    /// <returns>The fleet id associated with the instance, or null if no fleet is associated.</returns>
    private static async Task<string?> GetFleetIdApiCall(AmazonEC2Client client, Instance instance)
    {
        string? fleetId = null;

        DescribeFleetsRequest describeFleetsRequest = new DescribeFleetsRequest();
        DescribeFleetsResponse? fleetResponse = await client.DescribeFleetsAsync(describeFleetsRequest);
        Console.WriteLine($"Fleet response: {fleetResponse}");
        Console.WriteLine($"Fleets found: {fleetResponse.Fleets is not null}");
        Console.WriteLine($"Fleets found: {fleetResponse.Fleets}");

        if (fleetResponse.Fleets != null && fleetResponse.Fleets.Count != 0)
        {
            Console.WriteLine($"Fleets found: {fleetResponse.Fleets.Count}\nFirst fleet ID: {fleetResponse.Fleets[0].FleetId}");
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
}
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
//using Amazon.LicenseManager;
//using Amazon.LicenseManager.Model;
using Amazon.Util;
using System.Reflection;
using Microsoft.Extensions.Caching.Memory;

namespace Ec2TraceGenerator;

public static class Program {
    public static void Main(string[] args) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        // Create a path to congiguration files
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
        var serviceProvider = builder.Services.BuildServiceProvider();
        var memoryCache = serviceProvider.GetRequiredService<IMemoryCache>();

        // Get Telemetry receiver address and API key
        var apiKey = builder.Configuration["Beakpoint:Otel:ApiKey"];
        var url = builder.Configuration["Beakpoint:Otel:Url"] ?? throw new InvalidOperationException("Beakpoint Otel Url is not configured");

        // Get instance metadata
        var attributes = getAttributes(builder.Configuration, memoryCache).GetAwaiter().GetResult();

        // Add OpenTelemetry
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracingBuilder => tracingBuilder
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    //.AddService("Ec2TraceGenerator")
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
    static async Task<Dictionary<string, object>> getAttributes(IConfiguration configuration, IMemoryCache memoryCache)
    {
        // Get AWS credentials
        var accessKey = configuration["AWS:Credentials:AccessKeyId"];
        var secretKey = configuration["AWS:Credentials:SecretAccessKey"];
        var sessionToken = configuration["AWS:Credentials:SessionToken"];

        var regionName = EC2InstanceMetadata.AvailabilityZone.Substring(0, EC2InstanceMetadata.AvailabilityZone.Length - 1);

        var credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);

        var region = RegionEndpoint.GetBySystemName(regionName);

        // Initialize EC2 client
        var client = new AmazonEC2Client(credentials, new AmazonEC2Config
        {
            RegionEndpoint = region
        });

        // Initialize License Manager client
        //var licenseManagerClient = new AmazonLicenseManagerClient(credentials, new AmazonLicenseManagerConfig
        //{
        //    RegionEndpoint = region
        //});

        // Describe instance request
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId, out Instance? instance) || instance is null)
        {
            instance = await DescripeInstancesApiCall(client, EC2InstanceMetadata.InstanceId);
            memoryCache.Set(EC2InstanceMetadata.InstanceId, instance, new MemoryCacheEntryOptions());
        }

        // Clarify platform details request
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId, out string? platformDetails) || platformDetails is null)
        {
            platformDetails = await ClarifyPlatformDetailsAsync(client, instance.ImageId, memoryCache, instance.PlatformDetails);
            memoryCache.Set(EC2InstanceMetadata.InstanceId, platformDetails, new MemoryCacheEntryOptions());
        }

        // Describe capacity reservation request to ger capacity reservation preference
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId, out string? capacityReservationPreference)
            || string.IsNullOrEmpty(capacityReservationPreference))
        {
            capacityReservationPreference = await DescribeCapacityReservationsApiCall(client, instance);
            memoryCache.Set(EC2InstanceMetadata.InstanceId, capacityReservationPreference, new MemoryCacheEntryOptions());
        }

        // Describe fleets request to get the fleet id
        if (!memoryCache.TryGetValue(EC2InstanceMetadata.InstanceId, out string? fleetId)
            || string.IsNullOrEmpty(fleetId))
        {
            fleetId = await GetFleetIdApiCall(client, instance);
            memoryCache.Set(EC2InstanceMetadata.InstanceId, fleetId, new MemoryCacheEntryOptions());
        }

        return new Dictionary<string, object>
        {
            ["aws.ec2.instance_id"] = instance.InstanceId,
            ["aws.ec2.instance_type"] = instance.InstanceType.Value,
            ["aws.region"] = regionName,
            ["aws.ec2.platform_details"] = platformDetails,
            ["aws.ec2.license_model"] = instance.Licenses.Count == 0 ? "No License required" : "Bring your own license", // Though I'm very unsure of it
            ["aws.ec2.tenancy"] = instance.Placement.Tenancy.Value,
            ["aws.ec2.instance_lifecycle"] = instance.InstanceLifecycle.Value,
            ["aws.ec2.capacity_reservation_id"] = instance.CapacityReservationId,
            ["aws.ec2.capacity_reservation_preference"] = capacityReservationPreference,
            ["aws.ec2.fleet_id"] = fleetId
        };
    }

    static async Task<Instance> DescripeInstancesApiCall(AmazonEC2Client client, string instanceId)
    {
        var request = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
        ? new DescribeInstancesRequest()
        : new DescribeInstancesRequest { InstanceIds = new List<string> { instanceId } };

        var response = await client.DescribeInstancesAsync(request);

        if (response.Reservations.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
                ? "No EC2 instances found in the account"
                : $"EC2 instance with id '{EC2InstanceMetadata.InstanceId}' not found";

            throw new InvalidOperationException(message);
        }

        return response.Reservations.First().Instances.First();
    }

    static async Task<string> DescribeCapacityReservationsApiCall(AmazonEC2Client client, Instance instance)
    {
        var capacityReservationPreference = "none";

        if (!string.IsNullOrEmpty(instance.CapacityReservationId))
        {
            var capacityReservationRequest = new DescribeCapacityReservationsRequest
            {
                CapacityReservationIds = new List<string> { instance.CapacityReservationId }
            };
            var capacityReservationResponse = await client.DescribeCapacityReservationsAsync(capacityReservationRequest);
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

    static async Task<string> GetFleetIdApiCall(AmazonEC2Client client, Instance instance)
    {
        var fleetId = string.Empty;

        var describeFleetsRequest = new DescribeFleetsRequest();
        var fleetResponse = await client.DescribeFleetsAsync(describeFleetsRequest);

        if (fleetResponse.Fleets != null && fleetResponse.Fleets.Count == 0)
        {
            foreach (var fleet in fleetResponse.Fleets)
            {
                var fleetInstancesRequest = new DescribeFleetInstancesRequest
                {
                    FleetId = fleet.FleetId
                };
                var fleetInstancesResponse = await client.DescribeFleetInstancesAsync(fleetInstancesRequest);
                if (fleetInstancesResponse.ActiveInstances.Any(i => i.InstanceId == instance.InstanceId))
                {
                    fleetId = fleet.FleetId;
                    break;
                }
            }
        }

        return fleetId;
    }

    static async Task<string> ClarifyPlatformDetailsAsync(AmazonEC2Client client, string imageId, IMemoryCache memoryCache, string originalPlatformDetails)
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
                var describeImagesRequest = new DescribeImagesRequest
                {
                    ImageIds = new List<string> { imageId }
                };
                var describeImagesResponse = await client.DescribeImagesAsync(describeImagesRequest);
                if (describeImagesResponse.Images.Count > 0)
                {
                    var image = describeImagesResponse.Images[0];
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
        return originalPlatformDetails; // Fallback if AMI lookup fails
    }
}

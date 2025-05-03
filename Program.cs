

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Amazon.Util;
using System;
using System.Threading.Tasks;

using System.Reflection;
using Microsoft.VisualBasic;

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


        var apiKey = builder.Configuration["Beakpoint:Otel:ApiKey"];
        var url = builder.Configuration["Beakpoint:Otel:Url"] ?? throw new InvalidOperationException("Beakpoint Otel Url is not configured");

        var attributes = getAttributes(builder.Configuration).GetAwaiter().GetResult();

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

    static async Task<Dictionary<string, object>> getAttributes(IConfiguration configuration)
    {
        var accessKey = configuration["AWS:Credentials:AccessKeyId"];
        var secretKey = configuration["AWS:Credentials:SecretAccessKey"];
        var sessionToken = configuration["AWS:Credentials:SessionToken"];

        var regionName = EC2InstanceMetadata.AvailabilityZone.Substring(0, EC2InstanceMetadata.AvailabilityZone.Length - 1);

        var credentials = new SessionAWSCredentials(accessKey, secretKey, sessionToken);

        var region = RegionEndpoint.GetBySystemName(regionName);

        var client = new AmazonEC2Client(credentials, new AmazonEC2Config
        {
            RegionEndpoint = region
        });

        var request = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
        ? new DescribeInstancesRequest()
        : new DescribeInstancesRequest { InstanceIds = new List<string> { EC2InstanceMetadata.InstanceId } };

        var response = await client.DescribeInstancesAsync(request);

        if (response.Reservations.Count == 0)
        {
            var message = string.IsNullOrWhiteSpace(EC2InstanceMetadata.InstanceId)
                ? "No RDS instances found in the account"
                : $"RDS instance with id '{EC2InstanceMetadata.InstanceId}' not found";

            throw new InvalidOperationException(message);
        }

        var instance = response.Reservations.First().Instances.First();

        return new Dictionary<string, object>
        {
            ["aws.ec2.instance_id"] = instance.InstanceId,
            ["aws.ec2.instance_type"] = instance.InstanceType.Value,
            ["aws.region"] = regionName,
            ["aws.ec2.operating_system"] = instance.PlatformDetails,
            ["aws.ec2.tenancy"] = instance.Placement.Tenancy.Value
        };
    }
}

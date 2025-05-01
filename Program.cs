

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ec2TraceGenerator;

public static class Program {
    public static void Main(string[] args) {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracingBuilder => tracingBuilder
                .SetResourceBuilder(ResourceBuilder.CreateDefault()
                    .AddService("Ec2TraceGenerator"))
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddAWSInstrumentation()
                .AddSource("BlazorApp")
                .AddOtlpExporter(opts => {
                    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                    opts.ExportProcessorType = ExportProcessorType.Simple;
                    opts.Endpoint = new Uri(builder.Configuration.GetValue<string>("Beakpoint:Otel:Url")!);
                    opts.Headers = $"x-bkpt-key={builder.Configuration.GetValue<string>("Beakpoint:Otel:ApiKey")}";
                }));

        WebApplication app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment()) {
            app.UseExceptionHandler("/Error");
        }

        //app.UseAntiforgery();

        //app.MapStaticAssets();

        app.MapGet("/", () => "Hello World!");

        app.Run();
    }
}

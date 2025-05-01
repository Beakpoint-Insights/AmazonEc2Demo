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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AmazonEc2Demo.Services;

/// <summary>
/// Hosted service responsible for initializing and configuring OpenTelemetry tracing
/// with OTLP exporter for the <c>RdsTraceGenerator</c>.
/// </summary>
public class OpenTelemetryService : IHostedService
{
    /// <summary>
    /// Represents the OpenTelemetry TracerProvider instance used to configure and manage tracing,
    /// including resource information, registered sources, samplers, and exporters.
    /// This variable is initialized during the service startup with telemetry configurations
    /// required for tracing events within the application.
    /// </summary>
    private TracerProvider? _tracerProvider;

    /// <summary>
    /// Represents the configuration settings for the application, sourced
    /// from various providers such as appsettings files, environment variables,
    /// or other configuration sources. Utilized to retrieve configuration values
    /// needed for setting up OpenTelemetry tracing, including endpoint and API key.
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Hosted service responsible for initializing and configuring OpenTelemetry tracing
    /// with OTLP exporter for the <c>RdsTraceGenerator</c>.
    /// </summary>
    public OpenTelemetryService(IConfiguration configuration)
    {
        _configuration = configuration;
    }
    
    /// <summary>
    /// Starts the OpenTelemetry TracerProvider on application startup.
    /// Reads OTLP endpoint and API key from environment variables,
    /// configures OTLP exporter and registers the <c>RdsTraceGenerator</c> source.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (not used).</param>
    /// <returns>A completed <see cref="Task" /> once the provider is initialized.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if required environment variables <c>OTEL_ENDPOINT</c> or <c>API_KEY</c> are not set.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var otelEndpoint = _configuration["BeakpointInsights:OTEL_ENDPOINT"] ??
                           throw new InvalidOperationException("BeakpointInsights:OTEL_ENDPOINT configuration variable is not set");

        var apiKey = _configuration["BeakpointInsights:API_KEY"] ??
                     throw new InvalidOperationException("BeakpointInsights:API_KEY configuration variable is not set");

        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("RdsTraceGenerator"))
            .AddSource("RdsTraceGenerator")
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = new Uri(otelEndpoint);
                opts.Headers = $"x-bkpt-key={apiKey}";
                opts.Protocol = OtlpExportProtocol.HttpProtobuf;
                opts.ExportProcessorType = ExportProcessorType.Simple;
            })
            .Build();

        Console.WriteLine("TracerProvider initialized");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes the OpenTelemetry <see cref="TracerProvider" /> when the application stops.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (not used).</param>
    /// <returns>A completed <see cref="Task" /> once the provider is disposed.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tracerProvider?.Dispose();
        return Task.CompletedTask;
    }
}
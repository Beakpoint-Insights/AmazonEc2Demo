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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AmazonEc2Demo.Services;

/// <summary>
/// Hosted service that triggers the generation of a single OpenTelemetry trace
/// with real Amazon RDS metadata during application startup.
/// </summary>
/// <param name="generator">An instance of <see cref="RdsTraceGenerator" /> used to generate the trace.</param>
/// <param name="logger">Logger for writing informational and error messages.</param>
public class TraceHostedService(Ec2TraceGenerator generator, ILogger<TraceHostedService> logger) : IHostedService
{
    /// <summary>
    /// Starts the hosted service.
    /// Invokes the <see cref="RdsTraceGenerator.GenerateTraceWithMetadataAsync" /> method to generate the trace.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (not used).</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting demo - generating a single trace with real environment metadata");

        await generator.GenerateTraceWithMetadataAsync();

        logger.LogInformation("Demo trace generated. Exiting.");
    }

    /// <summary>
    /// Stops the hosted service.
    /// No specific cleanup is required.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token (not used).</param>
    /// <returns>Completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
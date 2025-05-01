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
using System.Reflection;
using AmazonEc2Demo;
using AmazonEc2Demo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Setup activities that will be used to generate traces
Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

// Host setup, console args ingestion for configuration and runtime options
var builder = Host.CreateApplicationBuilder(args);

// Create a path to congiguration files
var basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
var projectPath = Path.GetFullPath(Path.Combine(basePath, Path.Combine("..", "..", "..")));

// Configuration setup
builder.Configuration
    .SetBasePath(projectPath)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables(); 

// Logging
builder.Services.AddLogging(logging => logging.AddConsole());

// Service registration
builder.Services.AddSingleton<AwsCredentialsProvider>();
builder.Services.AddSingleton(new ActivitySource("RdsTraceGenerator"));
builder.Services.AddSingleton<Ec2TraceGenerator>();

// Hosted service registration
builder.Services.AddHostedService<OpenTelemetryService>();
builder.Services.AddHostedService<TraceHostedService>();

var host = builder.Build();
await host.RunAsync();
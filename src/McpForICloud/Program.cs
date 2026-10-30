using McpForICloud;
using McpForICloud.Calendar;
using McpForICloud.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so every log line must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(McpForICloudOptions.Load());
builder.Services.AddSingleton<CredentialProvider>();
builder.Services.AddSingleton<MailService>();
builder.Services.AddSingleton<CalDavClient>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "mcp-for-icloud", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

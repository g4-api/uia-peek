using CommandBridge;

using G4.Converters;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;

using System;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using UiaPeek.Domain;
using UiaPeek.Domain.Extensions;
using UiaPeek.Domain.Formatters;
using UiaPeek.Domain.Hubs;
using UiaPeek.Domain.Middlewares;

// Attempt to resolve a command from the provided arguments.
var command = CommandBase.FindCommand(args);

// Early exit if a command was found and invoked.
if (command != null)
{
    // Invoke the resolved command with the same arguments.
    command.Invoke(args);

    // Exit the application after command execution.
    return;
}

// Write the ASCII logo for the Hub Controller with the specified version.
ControllerUtilities.WriteAsciiLogo(version: "0000.00.00.0000");

// Create a new instance of the WebApplicationBuilder with the provided command-line arguments.
var builder = WebApplication.CreateBuilder(args);

#region *** Url & Kestrel ***
// Configure the URLs that the Kestrel web server should listen on.
// If no URLs are specified, it uses the default settings.
builder.WebHost.UseKestrel();
#endregion

#region *** Service       ***
// Add response compression services to reduce the size of HTTP responses.
// This is enabled for HTTPS requests to improve performance.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// Add routing services with configuration to use lowercase URLs for consistency and SEO benefits.
builder.Services.AddRouting(i => i.LowercaseUrls = true);

// Add support for Razor Pages, enabling server-side rendering of web pages.
builder.Services.AddRazorPages();

// Enable directory browsing, allowing users to see the list of files in a directory.
builder.Services.AddDirectoryBrowser();

// Add controller services with custom input formatters and JSON serialization options.
builder.Services
    .AddControllers(i =>
        // Add a custom input formatter to handle plain text inputs.
        i.InputFormatters.Add(new PlainTextInputFormatter()))
    .AddJsonOptions(i =>
    {
        // Add a custom type converter for handling specific types during serialization/deserialization.
        i.JsonSerializerOptions.Converters.Add(new TypeConverter());

        // Add a custom exception converter to handle exception serialization.
        i.JsonSerializerOptions.Converters.Add(new ExceptionConverter());

        // Add a custom DateTime converter to handle ISO 8601 date/time format.
        i.JsonSerializerOptions.Converters.Add(new DateTimeIso8601Converter());

        // Add a custom method base converter to handle method base serialization.
        i.JsonSerializerOptions.Converters.Add(new MethodBaseConverter());

        // Add a custom dictionary converter to handle serialization of dictionaries with string keys and object values.
        i.JsonSerializerOptions.Converters.Add(new DictionaryStringObjectJsonConverter());

        // Ignore properties with null values during serialization to reduce payload size.
        i.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // Use a relaxed JSON escaping strategy to allow special characters in the output.
        i.JsonSerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        // Enable case-insensitive property name matching during deserialization.
        i.JsonSerializerOptions.PropertyNameCaseInsensitive = true;

        // Use camelCase naming for JSON properties to follow JavaScript conventions.
        i.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;

        // Configure JSON serializer to format JSON with indentation for readability.
        i.JsonSerializerOptions.WriteIndented = false;
    });

// Add and configure Swagger for API documentation and testing.
builder.Services.AddSwaggerGen(i =>
{
    // Define a Swagger document named "v4" with title and version information.
    i.SwaggerDoc(
        name: $"v4",
        info: new OpenApiInfo { Title = "G4™ Hub Controllers", Version = $"v4" });

    // Order API actions in the Swagger UI by HTTP method for better organization.
    i.OrderActionsBy(a => a.HttpMethod);

    // Enable annotations to allow for additional metadata in Swagger documentation.
    i.EnableAnnotations();
});

// Configure cookie policy options to manage user consent and cookie behavior.
builder.Services.Configure<CookiePolicyOptions>(i =>
{
    // Determine whether user consent is required for non-essential cookies.
    i.CheckConsentNeeded = _ => true;

    // Set the minimum SameSite policy to None, allowing cookies to be sent with cross-site requests.
    i.MinimumSameSitePolicy = SameSiteMode.None;
});

// Get origins from environment variable (with semicolon separation)
var originsEnvironmentParameter = Environment.GetEnvironmentVariable("ORIGINS");

// Normalize origins from environment variable or configuration
var origins = string.IsNullOrEmpty(originsEnvironmentParameter)
    ? builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? []
    : originsEnvironmentParameter.Split(";", StringSplitOptions.TrimEntries);

// Add and configure CORS (Cross-Origin Resource Sharing) to allow requests from any origin.
builder.Services.AddCors(options =>
    options.AddPolicy("CorsPolicy", policy => policy
        .SetIsOriginAllowed(origin =>
            origins.Contains(origin)
            || (origin != null && origin.StartsWith("vscode-webview://"))
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()
    )
);

// Add and configure SignalR for real-time web functionalities.
builder.Services
    .AddSignalR((i) =>
    {
        // Enable detailed error messages for debugging purposes.
        i.EnableDetailedErrors = true;

        // Set the maximum size of incoming messages to the largest possible value.
        i.MaximumReceiveMessageSize = long.MaxValue;

        // How often the server sends a keep-alive ping. Default is 15 seconds.
        i.KeepAliveInterval = TimeSpan.FromSeconds(15);

        // If the server hasn't heard from a client in this much time, it might consider the client disconnected.
        // Usually the clientTimeout is set higher than KeepAliveInterval.
        i.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    })
    .AddJsonProtocol((i) =>
    {
        i.PayloadSerializerOptions = new JsonSerializerOptions
        {
            // Configure JSON serializer to format JSON with indentation for readability.
            WriteIndented = false,

            // Ignore properties with null values during serialization to reduce payload size.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Use camelCase naming for JSON properties to follow JavaScript conventions.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

            // Enable case-insensitive property name matching during deserialization.
            PropertyNameCaseInsensitive = true,

            // Add a custom type converter for handling specific types during serialization/deserialization.
            Converters =
            {
                new TypeConverter(),
                new ExceptionConverter(),
                new DateTimeIso8601Converter(),
                new MethodBaseConverter()
            }
        };
    });

// Add a hosted service for capturing global keyboard and mouse events.
builder.Services.AddHostedService<EventCaptureService>();

// Add IHttpClientFactory to the service collection for making HTTP requests.
builder.Services.AddHttpClient();
#endregion

#region *** Dependencies  ***
builder.Services.AddTransient<IUiaPeekRepository, UiaPeekRepository>();
#endregion

#region *** Configuration ***
// Initialize the application builder
var app = builder.Build();

// Configure the application to use the response caching middleware
app.UseResponseCaching();

// Add the cookie policy
app.UseCookiePolicy();

// Add the routing and controller mapping to the application
app.UseRouting();

// Add the CORS policy to the application to allow cross-origin requests
app.UseCors("CorsPolicy");

// Add the Swagger UI for the main G4 API
// Add the Swagger documentation and UI page to the application
app.UseSwagger();
app.UseSwaggerUI(i =>
{
    i.SwaggerEndpoint($"/swagger/v4/swagger.json", $"G4");
    i.DisplayRequestDuration();
    i.EnableFilter();
    i.EnableTryItOutByDefault();
});

app.MapDefaultControllerRoute();
app.MapControllers();

// Add the SignalR hub to the application for real-time communication with clients and other services
app.MapHub<PeekHub>($"/hub/v4/g4/peek").RequireCors("CorsPolicy");
#endregion

// Start the application and wait for it to finish.
await app.RunAsync();

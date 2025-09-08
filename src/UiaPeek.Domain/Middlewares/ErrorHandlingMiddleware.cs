using G4.Extensions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using UiaPeek.Domain.Models;

namespace UiaPeek.Domain.Middlewares
{
    /// <summary>
    /// Middleware to handle exceptions and enhance error responses in the HTTP request pipeline.
    /// </summary>
    /// <param name="logger">The logger instance for logging information.</param>
    /// <param name="next">The next middleware in the pipeline.</param>
    public class ErrorHandlingMiddleware(ILogger<ErrorHandlingMiddleware> logger, RequestDelegate next)
    {
        #region *** Fields  ***
        // Options for JSON serialization used when formatting error responses.
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            // Configure JSON serializer to format JSON with indentation for readability.
            WriteIndented = false,

            // Ignore properties with null values during serialization to reduce payload size.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

            // Use camelCase naming for JSON properties to follow JavaScript conventions.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Represents the next middleware component in the HTTP request pipeline.
        private readonly RequestDelegate _next = next;

        // Logger instance used for logging information and errors within the middleware.
        private readonly ILogger<ErrorHandlingMiddleware> _logger = logger;
        #endregion

        #region *** Methods ***
        /// <summary>
        /// Invokes the middleware to handle the HTTP request and response.
        /// </summary>
        /// <param name="context">The current HTTP context.</param>
        /// <returns>A task that represents the completion of request processing.</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            // Backup the original response body stream
            var originalBodyStream = context.Response.Body;

            // Create a new memory stream to temporarily hold the response
            await using var memoryStream = new MemoryStream();

            // Replace the response body with the memory stream to capture the response
            context.Response.Body = memoryStream;

            try
            {
                // Invoke the next middleware in the pipeline
                await _next(context);

                // Reset the memory stream position to read the response from the beginning
                memoryStream.Seek(0, SeekOrigin.Begin);

                // Read the entire response body as a string
                var responseBody = await new StreamReader(memoryStream).ReadToEndAsync();

                // TODO: Handle special cases where the response body might contain multiple JSON objects
                // Replace any occurrences of "},{" with "," to ensure proper JSON formatting
                responseBody = Regex.Replace(
                    input: responseBody,
                    pattern: @"(?i),({?)""traceId"".*?}",
                    replacement: string.Empty);

                // Reset the memory stream position again for potential further use
                memoryStream.Seek(0, SeekOrigin.Begin);

                // Determine if the response status code indicates success (<400)
                var isSuccess = context.Response.StatusCode < 400;

                // Check if the response body is in JSON format using a custom AssertJson() extension method
                var isJsonResponse = responseBody.AssertJson();

                if (isSuccess)
                {
                    // If the response is successful, copy the original response back to the response stream
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    await memoryStream.CopyToAsync(originalBodyStream);
                    return;
                }

                // Initialize a variable to hold the error response model
                GenericErrorModel errorResponse;

                if (isJsonResponse)
                {
                    // If the response is JSON, attempt to deserialize it into the GenericErrorModel
                    errorResponse = JsonSerializer.Deserialize<GenericErrorModel>(responseBody, s_jsonOptions) ?? new GenericErrorModel(context)
                    {
                        Status = context.Response.StatusCode,
                        Title = "An error occurred.",
                        Detail = "No additional details available."
                    };
                }
                else
                {
                    // If the response is not JSON, create a new GenericErrorModel with default error details
                    errorResponse = new GenericErrorModel(context)
                    {
                        Status = context.Response.StatusCode,
                        Title = "An error occurred.",
                        Detail = "No additional details available."
                    };
                }

                // Extract route data from the current HTTP context and convert it to a dictionary
                var routeData = context.GetRouteData().Values.ToDictionary(k => k.Key, v => v.Value);

                // Add the extracted route data to the error response model
                errorResponse.AddRouteData(routeData);

                // Serialize the modified error response model back to a JSON string
                var modifiedResponse = JsonSerializer.Serialize(errorResponse, s_jsonOptions);

                // Update the Content-Length header to reflect the size of the modified response
                context.Response.ContentLength = System.Text.Encoding.UTF8.GetByteCount(modifiedResponse);

                // Restore the original response body stream
                context.Response.Body = originalBodyStream;

                // Set the Content-Type header to indicate a JSON response with problem details
                context.Response.ContentType = MediaTypeNames.Application.ProblemJson;

                // Write the modified JSON error response to the response body
                await context.Response.WriteAsync(modifiedResponse);
            }
            catch (Exception e)
            {
                // Log the exception details for debugging and monitoring purposes
                _logger.LogError(e, "An unhandled exception occurred.");

                // Restore the original response body stream in case of an exception
                context.Response.Body = originalBodyStream;

                // Handle the exception by generating an appropriate error response
                await HandleExceptionAsync(context, exception: e, s_jsonOptions);
            }
        }

        // Handles exceptions by generating and writing a standardized JSON error response to the HTTP context.
        private static Task HandleExceptionAsync(HttpContext context, Exception exception, JsonSerializerOptions jsonOptions)
        {
            // Set the Content-Type header to indicate a JSON response with problem details
            context.Response.ContentType = MediaTypeNames.Application.ProblemJson;

            // Determine the HTTP status code and error title based on the exception type
            (int statusCode, string title) = exception.GetBaseException().GetType().Name switch
            {
                "InvalidCredentialException" => (StatusCodes.Status401Unauthorized, "Invalid credentials provided."),
                "NoAvailableRunningTimeException" => (StatusCodes.Status403Forbidden, "No available running time."),
                _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
            };

            // Set the HTTP status code in the response
            context.Response.StatusCode = statusCode;

            // Retrieve the base exception to get the root cause of the exception
            var baseException = exception.GetBaseException();

            // Extract route data from the current HTTP context and convert it to a dictionary
            var routeData = context.GetRouteData().Values.ToDictionary(k => k.Key, v => v.Value);

            // Initialize a new GenericErrorModel with the current context
            var response = new GenericErrorModel(context)
            {
                Status = context.Response.StatusCode,
                Title = title,
                Detail = baseException.Message
            };

            // Add the extracted route data to the error response model
            response.AddRouteData(routeData);

            // Add the exception details to the error response model
            response.AddErrors(new Dictionary<string, string[]>
            {
                [baseException.GetType().Name] = [baseException.ToString()]
            });

            // Serialize the error response model to a JSON string using the provided JSON serializer options
            var jsonResponse = JsonSerializer.Serialize(response, jsonOptions);

            // Write the JSON error response to the HTTP response body asynchronously
            return context.Response.WriteAsync(jsonResponse);
        }
        #endregion
    }
}

using G4.Converters;

using Microsoft.Extensions.Configuration;

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChromiumPeek.Settings
{
    /// <summary>
    /// Represents the application settings including configuration, JSON options, and LiteDB connection.
    /// </summary>
    public static class AppSettings
    {
        #region *** Constants ***
        /// <summary>
        /// The version of the API.
        /// </summary>
        public const string ApiVersion = "4";
        #endregion

        #region *** Fields    ***
        /// <summary>
        /// Gets the application configuration.
        /// </summary>
        public static readonly IConfigurationRoot Configuration = NewConfiguraion();

        /// <summary>
        /// Gets the JSON serialization options.
        /// </summary>
        public static readonly JsonSerializerOptions JsonOptions = NewJsonOptions();
        #endregion

        #region *** Methods   ***
        // Creates a new instance of IConfigurationRoot by configuring it with settings from appsettings.json and environment variables.
        private static IConfigurationRoot NewConfiguraion()
        {
            // Create a new ConfigurationBuilder instance
            new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(path: "appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            // Create a new ConfigurationBuilder instance
            var configurationBuilder = new ConfigurationBuilder();

            // Set the base path for the configuration file to the current directory
            configurationBuilder.SetBasePath(Directory.GetCurrentDirectory());

            // Add the appsettings.json file as a configuration source, if it exists (optional), without reloading it on change
            configurationBuilder.AddJsonFile(path: "appsettings.json", optional: true, reloadOnChange: false);

            // Add environment variables as a configuration source
            configurationBuilder.AddEnvironmentVariables();

            // Build and return the IConfigurationRoot instance
            return configurationBuilder.Build();
        }

        // Creates a new instance of JsonSerializerOptions with custom settings and converters.
        private static JsonSerializerOptions NewJsonOptions()
        {
            // Initialize JSON serialization options.
            var jsonOptions = new JsonSerializerOptions()
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            // Add a custom exception converter
            jsonOptions.Converters.Add(new ExceptionConverter());

            // Add a custom method base converter
            jsonOptions.Converters.Add(new MethodBaseConverter());

            // Add a custom type converter
            jsonOptions.Converters.Add(new TypeConverter());

            // Add a custom DateTime converter for ISO 8601 format (yyyy-MM-ddTHH:mm:ss.ffffffK)
            jsonOptions.Converters.Add(new DateTimeIso8601Converter());

            // Return the JSON options with custom settings and converters added
            return jsonOptions;
        }
        #endregion
    }
}

using G4.Converters;

using Microsoft.Extensions.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        // Retrieves a value from an environment variable and converts it to the specified type <typeparamref name="T"/>.
        // If the environment variable is not found, empty, or cannot be converted, returns the provided default value.
        private static T GetOrDefault<T>(string environmentParameter, T defaultValue)
        {
            // Attempt to read the environment variable value
            var envValue = Environment.GetEnvironmentVariable(environmentParameter);

            // If the environment variable is missing or blank, use the default value
            if (string.IsNullOrWhiteSpace(envValue))
            {
                return defaultValue;
            }

            try
            {
                // Check if T is a nullable type and get the underlying type
                var underlyingType = Nullable.GetUnderlyingType(typeof(T));
                var targetType = underlyingType ?? typeof(T);

                // Special handling for booleans to support "true", "false", "1", and "0"
                if (targetType == typeof(bool))
                {
                    if (bool.TryParse(envValue, out bool boolResult))
                    {
                        return (T)(object)boolResult;
                    }

                    // Support numeric boolean representation
                    if (envValue.Trim() == "1") return (T)(object)true;
                    if (envValue.Trim() == "0") return (T)(object)false;
                }

                // Attempt to convert the string value to the target type using system conversion
                return (T)Convert.ChangeType(envValue.Trim(), targetType);
            }
            catch
            {
                // If conversion fails (e.g., invalid format), fall back to the default value
                return defaultValue;
            }
        }
        #endregion
    }
}

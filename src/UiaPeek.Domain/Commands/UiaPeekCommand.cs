using CommandBridge;

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using UiaPeek.Domain;

namespace UiaPeek.Domain.Commands
{
    /// <summary>
    /// A command that inspects a screen coordinate and prints the UI Automation
    /// ancestor chain of the element found at that point.
    /// </summary>
    [Command(
        name: "peek",
        description: "Retrieve the ancestor chain of a UI Automation element at the given screen coordinates, " +
            "or the currently focused element if coordinates are not provided.")]
    public class UiaPeekCommand() : CommandBase(s_commands)
    {
        // Defines the command schema and parameter metadata.
        private static readonly Dictionary<string, IDictionary<string, CommandData>> s_commands =
            new(StringComparer.Ordinal)
            {
                ["peek"] = new Dictionary<string, CommandData>(StringComparer.Ordinal)
                {
                    ["f"] = new()
                    {
                        Name = "focused",
                        Description = "Peek the currently focused element instead of using coordinates.",
                        Mandatory = false
                    },
                    ["x"] = new()
                    {
                        Name = "xpos",
                        Description = "X-coordinate on the screen.",
                        Mandatory = false
                    },
                    ["y"] = new()
                    {
                        Name = "ypos",
                        Description = "Y-coordinate on the screen.",
                        Mandatory = false
                    }
                }
            };

        // JSON serialization options used for output.
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <inheritdoc />
        protected override void OnInvoke(Dictionary<string, string> parameters)
        {
            // Exit early if parameters are missing or insufficient.
            if (parameters == null || parameters.Count < 1)
            {
                // Not enough parameters — return empty JSON.
                Console.WriteLine("{}");
                return;
            }

            // Validate presence and format of X and Y coordinates.
            var isX = parameters.TryGetValue("xpos", out var xOut) && Regex.IsMatch(xOut, @"^(-)?\d+$");
            var isY = parameters.TryGetValue("ypos", out var yOut) && Regex.IsMatch(xOut, @"^(-)?\d+$");
            var isFocused = parameters.ContainsKey("focused");

            // Parse X coordinate (defaults to 0 if missing or invalid).
            var x = isX && int.TryParse(xOut, out var xValue)
                ? xValue
                : 0;

            // Parse Y coordinate (defaults to 0 if missing or invalid).
            var y = isY && int.TryParse(yOut, out var yValue)
                ? yValue
                : 0;

            // Retrieve the ancestor chain based on the provided coordinates
            // or focused element if no coordinates.
            var chain = (!isX || !isY) && isFocused
                ? new UiaPeekRepository().Peek()
                : new UiaPeekRepository().Peek(x, y);

            // Serialize the result to JSON and write to console.
            var json = JsonSerializer.Serialize(chain, s_jsonOptions);

            // Output the JSON result to the console.
            Console.WriteLine(json);
        }
    }
}

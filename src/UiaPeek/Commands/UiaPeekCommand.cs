using CommandBridge;

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

using UiaPeek.Domain;

namespace UiaPeek.Commands
{
    /// <summary>
    /// A command that inspects a screen coordinate and prints the UI Automation
    /// ancestor chain of the element found at that point.
    /// </summary>
    [Command(name: "peek", description: "Retrieve the ancestor chain of a UI Automation element at the given screen coordinates.")]
    public class UiaPeekCommand() : CommandBase(s_commands)
    {
        // Defines the command schema and parameter metadata.
        private static readonly Dictionary<string, IDictionary<string, CommandData>> s_commands =
            new(StringComparer.Ordinal)
            {
                ["peek"] = new Dictionary<string, CommandData>(StringComparer.Ordinal)
                {
                    ["x"] = new()
                    {
                        Name = "xpos",
                        Description = "X-coordinate on the screen.",
                        Mandatory = true
                    },
                    ["y"] = new()
                    {
                        Name = "ypos",
                        Description = "Y-coordinate on the screen.",
                        Mandatory = true
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
            if (parameters == null || parameters.Count < 2)
            {
                // Not enough parameters — return empty JSON.
                Console.WriteLine("{}");
                return;
            }

            // Parse X coordinate (defaults to 0 if missing or invalid).
            var x = parameters.TryGetValue("xpos", out var xOut) && int.TryParse(xOut, out var xValue)
                ? xValue
                : 0;

            // Parse Y coordinate (defaults to 0 if missing or invalid).
            var y = parameters.TryGetValue("ypos", out var yOut) && int.TryParse(yOut, out var yValue)
                ? yValue
                : 0;

            // Get ancestor chain at the specified coordinates.
            var chain = new UiaPeekRepository().Peek(x, y);

            // Serialize the result to JSON and write to console.
            var json = JsonSerializer.Serialize(chain, s_jsonOptions);

            // Output the JSON result to the console.
            Console.WriteLine(json);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace UiaPeek.Domain.Extensions
{
    /// <summary>
    /// Provides utility methods for controllers in the G4™ application.
    /// </summary>
    public static class ControllerUtilities
    {
        /// <summary>
        /// Writes the G4™ UiaPeek ASCII logo to the console, including the specified version number.
        /// </summary>
        /// <param name="version">The version number to display in the logo.</param>
        public static void WriteAsciiLogo(string version)
        {
            // Define the ASCII art logo with placeholders for version information.
            var logo = new string[]
            {
                "  _   _ _       ____           _              ",
                " | | | (_) __ _|  _ \\ ___  ___| | __         ",
                " | | | | |/ _` | |_) / _ \\/ _ \\ |/ /        ",
                " | |_| | | (_| |  __/  __/  __/   <           ",
                "  \\___/|_|\\__,_|_|   \\___|\\___|_|\\_\\    ",
                "                                              ",
                "            G4™ - UIA Recorder                ",
                "            Powered by IUIAutomation          ",
                "                                              ",
                "  Version: " + version + "                    ",
                "  Project: https://github.com/g4-api/uia-peek ",
                "                                              "
             };

            // Clear the console before writing the logo to ensure a clean display.
            Console.Clear();

            // Set the console output encoding to UTF-8 to support Unicode characters.
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Output the logo to the console by joining the array elements with a newline character.
            Console.WriteLine(string.Join("\n", logo));
        }

        /// <summary>
        /// Retrieves the local endpoint's IP address.
        /// </summary>
        /// <returns>The IP address of the local endpoint.</returns>
        /// <exception cref="KeyNotFoundException">Thrown when the local endpoint IP address is not found.</exception>
        public static string GetLocalEndpoint()
        {
            try
            {
                // Get the host entry for the local machine
                var host = Dns.GetHostEntry(Dns.GetHostName());

                // Iterate through the list of IP addresses associated with the host
                foreach (var ip in host.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork))
                {
                    // If the IP address is not null or empty, return it
                    if (!string.IsNullOrEmpty(ip.ToString()))
                    {
                        return ip.ToString();
                    }
                }

                // Throw an exception if no valid IP address is found
                throw new KeyNotFoundException("No valid IP address found.");
            }
            catch
            {
                // Return an empty string if an exception occurs
                return string.Empty;
            }
        }
    }
}

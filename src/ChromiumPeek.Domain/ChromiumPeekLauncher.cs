using ChromiumPeek.Domain.Hubs;
using ChromiumPeek.Domain.Models;

using Microsoft.AspNetCore.SignalR;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ChromiumPeek.Domain
{
    /// <summary>
    /// Default <see cref="IChromiumPeekLauncher"/>. Launches Chromium with the recorder
    /// extension loaded and tracks the launched processes so they can be stopped safely.
    /// Registered as a singleton so the process registry persists across requests.
    /// </summary>
    /// <param name="hubContext">Hub context used to ask the recorder extension to close the browser.</param>
    public class ChromiumPeekLauncher(IHubContext<ChromiumPeekHub> hubContext) : IChromiumPeekLauncher
    {
        #region *** Constants ***
        // Fixed DevTools remote debugging port applied to every launch.
        private const int RemoteDebuggingPort = 9222;

        // Folder name (under the app base directory) that holds the recorder extension.
        private const string ExtensionFolderName = "ChromiumPeek.Extension";

        // Prefix for the per-launch Chromium user-data directory created under the temp folder.
        private const string UserDataDirectoryPrefix = "chromium-peek-";

        // How long to wait for the extension to close the browser before forcing a kill.
        private const int GracefulCloseTimeoutSeconds = 5;
        #endregion

        #region *** Fields    ***
        // Registry of processes started by this launcher, keyed by process id. Only ids in
        // this map may be stopped, so an arbitrary/unknown process id can never be killed.
        private readonly ConcurrentDictionary<int, Process> _startedProcesses = new();

        // Hub context used to push the CloseBrowser message to the connected recorder extension.
        private readonly IHubContext<ChromiumPeekHub> _hubContext = hubContext;
        #endregion

        #region *** Methods   ***
        /// <inheritdoc />
        public int Start(DriverParametersModel driverParameters)
        {
            // Resolve the recorder extension folder from the app base directory; the browser
            // is only useful for peek when the recorder extension is loaded.
            var baseDirectory = AppContext.BaseDirectory;
            var extensionDirectory = Path.Combine(baseDirectory, ExtensionFolderName);

            if (!Directory.Exists(extensionDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"Recorder extension folder not found: {extensionDirectory}");
            }

            // Resolve the browser binary from the client capabilities; it is required.
            var chromeOptions = driverParameters?.Capabilities?.AlwaysMatch?.ChromeOptions;
            var browserPath = chromeOptions?.Binary;

            if (string.IsNullOrWhiteSpace(browserPath) || !File.Exists(browserPath))
            {
                throw new FileNotFoundException(
                    "Browser binary not found. Provide capabilities.alwaysMatch.'goog:chromeOptions'.binary. " +
                    $"Value: '{browserPath}'.");
            }

            // Build the process with the peek base flags, then append the client arguments.
            var startInfo = new ProcessStartInfo
            {
                FileName = browserPath,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            startInfo.ArgumentList.Add($"--remote-debugging-port={RemoteDebuggingPort}");
            startInfo.ArgumentList.Add($"--load-extension={extensionDirectory}");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");

            // Append the client-supplied arguments (for example --disable-gpu). No initial
            // URL is opened; the client can navigate later via the debugging port.
            var isUserDataDirectoryProvided = false;

            foreach (var argument in chromeOptions.Args ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(argument))
                {
                    startInfo.ArgumentList.Add(argument);
                }

                // Track whether the client already supplied its own profile directory.
                if (argument != null && argument.StartsWith("--user-data-dir", StringComparison.OrdinalIgnoreCase))
                {
                    isUserDataDirectoryProvided = true;
                }
            }

            // A dedicated user-data directory is required for reliable remote debugging and stops
            // Chromium from handing its command line to an already-running instance and exiting
            // immediately (which would drop the process from the registry right after launch). Only
            // create one when the client did not supply its own.
            if (!isUserDataDirectoryProvided)
            {
                var userDataDirectory = Path.Combine(
                    Path.GetTempPath(),
                    UserDataDirectoryPrefix + Guid.NewGuid().ToString("n"));

                Directory.CreateDirectory(userDataDirectory);
                startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
            }

            // Launch the browser.
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the browser process.");

            // Capture the id used as the registry key.
            var startedProcessId = process.Id;

            // Register the process so it can be stopped later. Liveness is intentionally checked
            // at stop time rather than through Process.Exited: Chromium's launched process can
            // exit while the browser keeps running, so its exit is not a reliable "browser closed"
            // signal and must not auto-remove a still-live entry from the registry.
            _startedProcesses[startedProcessId] = process;

            return startedProcessId;
        }

        /// <inheritdoc />
        public async Task<bool> StopAsync(int processId)
        {
            // Only processes this launcher started may be stopped; unknown ids are rejected.
            if (!_startedProcesses.TryRemove(processId, out var process))
            {
                return false;
            }

            try
            {
                // The browser may have already closed on its own; report it as not-stopped
                // instead of pretending we killed a process that was no longer running.
                if (process.HasExited)
                {
                    return false;
                }

                // Ask the recorder extension to close the browser so Chromium shuts down cleanly.
                // A graceful close is preferred because Chromium spawns renderer/GPU/utility
                // processes that reparent out of the launched process's tree and can survive
                // Kill(entireProcessTree). The message is broadcast; with a single recorder browser
                // per launch only the intended instance is listening.
                await _hubContext.Clients.All.SendAsync(ChromiumPeekHub.CloseBrowserClientMethod);

                // Give the browser a short grace period to exit on its own after the close request.
                var isExited = await TryWaitForExitAsync(process, TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds));

                // Force a process-tree kill only when the graceful close did not take effect (for
                // example the extension was disconnected or asleep), so stop is always reliable.
                if (!isExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return true;
            }
            catch (InvalidOperationException)
            {
                // The process exited between the checks and the kill; treat as not-stopped.
                return false;
            }
            finally
            {
                process.Dispose();
            }
        }

        // Waits for the process to exit, returning true if it exited within the timeout and false
        // if the timeout elapsed first.
        private static async Task<bool> TryWaitForExitAsync(Process process, TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);

            try
            {
                await process.WaitForExitAsync(cancellation.Token);

                return true;
            }
            catch (OperationCanceledException)
            {
                // The grace period elapsed before the process exited.
                return false;
            }
        }
        #endregion
    }
}

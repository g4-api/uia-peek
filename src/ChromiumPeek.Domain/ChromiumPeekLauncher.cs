using ChromiumPeek.Domain.Models;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace ChromiumPeek.Domain
{
    /// <summary>
    /// Default <see cref="IChromiumPeekLauncher"/>. Launches Chromium with the recorder
    /// extension loaded and tracks the launched processes so they can be stopped safely.
    /// Registered as a singleton so the process registry persists across requests.
    /// </summary>
    public class ChromiumPeekLauncher : IChromiumPeekLauncher
    {
        #region *** Constants ***
        // Fixed DevTools remote debugging port applied to every launch.
        private const int RemoteDebuggingPort = 9222;

        // Folder name (under the app base directory) that holds the recorder extension.
        private const string ExtensionFolderName = "ChromiumPeek.Extension";
        #endregion

        #region *** Fields    ***
        // Registry of processes started by this launcher, keyed by process id. Only ids in
        // this map may be stopped, so an arbitrary/unknown process id can never be killed.
        private readonly ConcurrentDictionary<int, Process> _startedProcesses = new();
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
            foreach (var argument in chromeOptions.Args ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(argument))
                {
                    startInfo.ArgumentList.Add(argument);
                }
            }

            // Launch the browser.
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the browser process.");

            // Capture the id so the exit handler never touches a possibly-disposed process.
            var startedProcessId = process.Id;

            // Auto-remove the process from the registry when it exits on its own, so a later
            // stop for the same id correctly reports "unknown".
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => _startedProcesses.TryRemove(startedProcessId, out _);

            // Register the process so it can be stopped later.
            _startedProcesses[startedProcessId] = process;

            return startedProcessId;
        }

        /// <inheritdoc />
        public bool Stop(int processId)
        {
            // Only processes this launcher started may be stopped; unknown ids are rejected.
            if (!_startedProcesses.TryRemove(processId, out var process))
            {
                return false;
            }

            // Kill the whole tree (Chromium spawns child processes), tolerating a process
            // that has already exited, then release the handle.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process has already exited; nothing more to do.
            }
            finally
            {
                process.Dispose();
            }

            return true;
        }
        #endregion
    }
}

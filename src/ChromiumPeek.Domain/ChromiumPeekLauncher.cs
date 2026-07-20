using ChromiumPeek.Domain.Hubs;
using ChromiumPeek.Domain.Models;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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
    /// <param name="server">Server accessor used to resolve this server's own listening origin.</param>
    public class ChromiumPeekLauncher(IHubContext<ChromiumPeekHub> hubContext, IServer server) : IChromiumPeekLauncher
    {
        #region *** Constants ***
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

        // Server accessor used to resolve this server's own listening origin, which is injected
        // into each launched browser (via the bootstrap page) so its recorder extension connects
        // back to the exact server that launched it.
        private readonly IServer _server = server;
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

            // Use a per-launch free port so multiple recorder browsers can run at once without
            // colliding on a shared DevTools port.
            var remoteDebuggingPort = GetFreeTcpPort();
            startInfo.ArgumentList.Add($"--remote-debugging-port={remoteDebuggingPort}");
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

            // Open this server's own bootstrap page as the initial tab. Its content script reports
            // this origin's hub to the recorder extension, so the freshly launched browser connects
            // back to the exact server that launched it rather than the extension's default hub. A
            // positional (non-flag) argument is treated by Chromium as a URL to open.
            var bootstrapUrl = ResolveServerBootstrapUrl();

            if (!string.IsNullOrEmpty(bootstrapUrl))
            {
                startInfo.ArgumentList.Add(bootstrapUrl);
            }

            // Launch the browser without activating its window, so it appears on-screen but does not
            // steal foreground focus from the caller (for example the VS Code editor that started the
            // recording). The window show-state is carried through STARTUPINFO to CreateProcess.
            var startedProcessId = StartProcessNoActivate(startInfo.FileName, startInfo.ArgumentList);

            // Register the launched process so it can be stopped later. Liveness is intentionally
            // checked at stop time rather than through Process.Exited: Chromium's launched process can
            // exit while the browser keeps running, so its exit is not a reliable "browser closed"
            // signal and must not auto-remove a still-live entry from the registry. A managed handle
            // is resolved by id; when the initial process has already relaunched and exited, the
            // graceful-close broadcast in StopAsync is what stops the browser.
            try
            {
                _startedProcesses[startedProcessId] = Process.GetProcessById(startedProcessId);
            }
            catch (ArgumentException)
            {
                // The initial process already exited (Chromium relaunches into a separate process).
            }

            return startedProcessId;
        }

        /// <inheritdoc />
        public async Task<bool> StopAsync(int processId)
        {
            // Best-effort removal from the registry. The originally launched Chromium process
            // frequently exits on its own because Chromium relaunches the browser into a separate
            // process, so an absent or already-exited entry must NOT short-circuit the graceful
            // close below — doing so was why the extension never received the close request and the
            // still-running browser stayed open.
            _startedProcesses.TryRemove(processId, out var process);

            // Ask the recorder extension to close the browser, regardless of the launched process's
            // state. The extension closes its own windows, which quits Chromium because each launch
            // uses a dedicated user-data directory; this is what actually stops the browser when the
            // launched process id has already exited. The message is broadcast; with a single
            // recorder browser per host only the intended instance is listening.
            try
            {
                await _hubContext.Clients.All.SendAsync(ChromiumPeekHub.CloseBrowserClientMethod);
            }
            catch
            {
                // No connected extension to receive the close (for example it disconnected); the
                // force-kill fallback below still applies when we hold a live launched process.
            }

            // Force-kill the process tree only when the launched process is still alive and the
            // graceful close did not take effect (for example the extension was disconnected or
            // asleep). When the launched process already exited, the browser runs under processes we
            // no longer track, so the graceful close above is the mechanism that stops it.
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        var isExited = await TryWaitForExitAsync(process, TimeSpan.FromSeconds(GracefulCloseTimeoutSeconds));

                        if (!isExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the check and the kill; nothing more to do.
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Report that a stop was requested. The graceful close is broadcast in every case —
            // even when the launched process had already exited — so the browser is always asked to
            // close instead of returning a misleading "was not running".
            return true;
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

        // Resolves this server's own bootstrap page URL from its listening addresses, normalizing a
        // wildcard host (0.0.0.0, [::], +, *) to localhost so the launched browser can reach it.
        // Returns null when no usable address is available, in which case no initial page is opened.
        private string ResolveServerBootstrapUrl()
        {
            var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;

            if (addresses == null)
            {
                return null;
            }

            // Prefer an http address; the browser and hub communicate over http/ws on loopback.
            var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ?? addresses.FirstOrDefault();

            if (string.IsNullOrEmpty(address))
            {
                return null;
            }

            // Replace wildcard host tokens that a browser cannot reach with localhost.
            var normalized = address
                .Replace("://+", "://localhost")
                .Replace("://*", "://localhost")
                .Replace("://0.0.0.0", "://localhost")
                .Replace("://[::]", "://localhost");

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            {
                return null;
            }

            // Build the bootstrap page URL served from wwwroot on the same origin.
            var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/recorder-bootstrap.html");

            return builder.Uri.ToString();
        }

        // Picks an available loopback TCP port by binding to port 0 and reading the assigned port,
        // so each launch gets its own DevTools port and simultaneous browsers do not collide.
        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        // Launches a process with its window shown but not activated (SW_SHOWNOACTIVATE), so the new
        // window is visible on-screen without stealing foreground focus from the caller. Returns the
        // new process id.
        private static int StartProcessNoActivate(string fileName, IReadOnlyList<string> arguments)
        {
            // Build a properly quoted command line: the executable followed by each argument.
            var commandLine = BuildCommandLine(fileName, arguments);

            // Ask the OS to show the new window without activating it so focus stays with the caller.
            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESHOWWINDOW,
                wShowWindow = SW_SHOWNOACTIVATE
            };

            var isCreated = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                null,
                ref startupInfo,
                out var processInformation);

            if (!isCreated)
            {
                var errorCode = Marshal.GetLastWin32Error();
                throw new Win32Exception(errorCode, $"Failed to start the browser process (Win32 error {errorCode}).");
            }

            // The returned thread/process handles are not needed; the process is tracked by id.
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);

            return processInformation.dwProcessId;
        }

        // Builds a Windows command line (executable plus arguments) with each token quoted per the
        // CommandLineToArgvW rules, so paths with spaces and embedded quotes survive intact.
        private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
        {
            var builder = new StringBuilder();
            AppendArgument(builder, fileName);

            foreach (var argument in arguments)
            {
                builder.Append(' ');
                AppendArgument(builder, argument);
            }

            return builder.ToString();
        }

        // Appends one command-line argument, quoting and escaping it per the CommandLineToArgvW rules.
        private static void AppendArgument(StringBuilder builder, string argument)
        {
            // Arguments without whitespace or quotes need no quoting.
            if (argument.Length > 0 && argument.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                builder.Append(argument);
                return;
            }

            builder.Append('"');

            for (var index = 0; ; index++)
            {
                var backslashes = 0;

                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    // Escape trailing backslashes so they do not escape the closing quote.
                    builder.Append('\\', backslashes * 2);
                    break;
                }

                if (argument[index] == '"')
                {
                    // Escape the run of backslashes and the embedded quote.
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(argument[index]);
                }
            }

            builder.Append('"');
        }
        #endregion

        #region *** Interop   ***
        // STARTUPINFO.dwFlags bit that makes wShowWindow take effect.
        private const int STARTF_USESHOWWINDOW = 0x00000001;

        // ShowWindow command that displays a window without activating it (keeps the caller's focus).
        private const short SW_SHOWNOACTIVATE = 4;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
        #endregion
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using UiaPeek.Domain;

namespace UiaPeek.PathFinder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly UiaPeekRepository _domain = new();

        // Indicates whether the tracking is currently running.
        private bool _isRunning;
        private double _refreshSpeed = 1000;

        // Imports the GetPhysicalCursorPos function from the user32.dll library.
        // If the function succeeds, the return value is nonzero. If the function fails, the return value is zero.
        // To get extended error information, call Marshal.GetLastWin32Error.
        [LibraryImport("user32.dll")]
        private static partial IntPtr GetPhysicalCursorPos(out TagPoint lpPoint);

        public MainWindow()
        {
            InitializeComponent();
        }

        #region *** Start/Stop   ***
        /// <summary>
        /// Handles the Click event for the Start/Stop button.
        /// </summary>
        /// <param name="sender">The object that triggered the event.</param>
        /// <param name="e">The event arguments.</param>
        private void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            StartStop((Button)sender);
        }

        /// <summary>
        /// Handles the AccessKeyPressed event for the Start/Stop button.
        /// </summary>
        /// <param name="sender">The object that triggered the event.</param>
        /// <param name="e">The event arguments.</param>
        private void BtnStartStop_AccessKeyPressed(object sender, AccessKeyPressedEventArgs e)
        {
            StartStop((Button)sender);
        }

        // Handles the click event for the Start/Stop button.
        private void StartStop(Button startStopButton)
        {
            // Toggle the running state (true = running, false = stopped)
            _isRunning = !_isRunning;

            // Update the button's label to reflect the new state
            SetLabel(startStopButton);

            // Launch a background task to monitor the cursor position
            Task.Run(() =>
            {
                // Continue running until the toggle is set to false
                while (_isRunning)
                {
                    // Get the current physical cursor position (screen coordinates)
                    GetPhysicalCursorPos(out TagPoint point);

                    // Query the domain service for the UI element chain at the cursor position
                    var chain = _domain.Peek(point.X, point.Y);

                    // Extract the XPath-like locator from the element chain
                    var xpath = chain.Locator;

                    // Update the UI text box with the locator value on the UI thread
                    Dispatcher.BeginInvoke(() =>
                    {
                        TxbPath.Text = xpath;
                    });

                    // Delay the loop iteration to avoid excessive updates
                    Thread.Sleep(TimeSpan.FromMilliseconds(_refreshSpeed));
                }
            });
        }

        // Sets the label for a button based on the current state.
        private void SetLabel(Button button)
        {
            // If running, set the button label to indicate stopping
            if (_isRunning)
            {

                button.Content = "⬛ _Stop";
            }
            // If not running, set the button label to indicate starting
            else
            {
                button.Content = "▶ _Start";
            }
        }
        #endregion

        #region *** Set Speed    ***
        /// <summary>
        /// Handles the ValueChanged event of the SldRefreshSpeed slider, updating the refresh speed.
        /// </summary>
        /// <param name="sender">The sender of the event.</param>
        /// <param name="e">The event data.</param>
        private void SldRefreshSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // Update the refresh speed based on the slider value
            _refreshSpeed = ((Slider)sender).Value;
        }
        #endregion

        // Represents a point with integer X and Y coordinates.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TagPoint
        {
            /// <summary>
            /// Gets or sets the X coordinate of the point.
            /// </summary>
            public int X;

            /// <summary>
            /// Gets or sets the Y coordinate of the point.
            /// </summary>
            public int Y;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;

using UiaPeek.Domain;
using UiaPeek.PathFinder.Models;

using UIAutomationClient;

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

        // Imports the SetPhysicalCursorPos function from the user32.dll library.
        // If the function succeeds, the return value is nonzero. If the function fails, the return value is zero.
        // To get extended error information, call Marshal.GetLastWin32Error.
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetPhysicalCursorPos(int x, int y);

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
                        TxbAxisX.Text = point.X.ToString();
                        TxbAxisY.Text = point.Y.ToString();
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

        #region *** Set Position ***
        // Handles the Click event for the Set Position button.
        private void BtnSetPosition_Click(object sender, RoutedEventArgs e)
        {
            SetPosition();
        }

        // Handles the AccessKeyPressed event for the Set Position button.
        private void BtnSetPosition_AccessKeyPressed(object sender, AccessKeyPressedEventArgs e)
        {
            SetPosition();
        }

        // Handles the AccessKeyPressed event for the Set Position button.
        private void SetPosition()
        {
            // Use Dispatcher to update UI components
            Dispatcher.BeginInvoke(() =>
            {
                // Parse values from text boxes
                _ = int.TryParse(TxbAxisX.Text, out int x);
                _ = int.TryParse(TxbAxisY.Text, out int y);

                // Set the physical cursor position
                SetPhysicalCursorPos(x, y);
            });
        }
        #endregion

        #region *** Test Path    ***
        // Handles the Click event for the Test Path button.
        private void BtnTestPath_Click(object sender, RoutedEventArgs e)
        {
            TestPath(sender, e);
        }

        // Handles the AccessKeyPressed event for the Test Path button.
        private void BtnTestPath_AccessKeyPressed(object sender, AccessKeyPressedEventArgs e)
        {
            TestPath(sender, e);
        }

        // Handles the "Test Path" button click by attempting to resolve a UI Automation
        // element from the provided XPath string. 
        private async void TestPath(object sender, RoutedEventArgs e)
        {
            // Prevent multiple concurrent tests
            BtnTestPath.IsEnabled = false;

            // Initial UI status setup
            LblStatus.Content = "Testing...";
            LblStatus.Foreground = Brushes.Black;
            LblStatus.Visibility = Visibility.Visible;

            // Trim user-provided XPath from textbox
            var xpath = TxbPath.Text.Trim();

            // Start stopwatch for performance measurement
            var sw = Stopwatch.StartNew();

            // A lightweight timer to tick the label every 100ms
            // Helps reassure the user the app is still responsive
            var ticker = new System.Timers.Timer(100) { AutoReset = true };
            ticker.Start();

            try
            {
                // Run the heavy automation lookup off the UI thread
                var found = await Task.Run(() =>
                {
                    var automation = new CUIAutomation8();

                    // Attempt to locate the element by XPath
                    var element = automation.GetElement(xpath);

                    // Return true if element was located
                    return element != null;
                });

                // Stop stopwatch and ticker after background task completes
                sw.Stop();
                ticker.Stop();

                // Safely update UI thread with results
                LblStatus.Foreground = found ? Brushes.Green : Brushes.Red;
                LblStatus.Content = found ? "Found" : "Not Found";
                LblTime.Content = $"{sw.Elapsed.TotalSeconds:0.000}s";
            }
            catch (Exception)
            {
                // Stop ticker to prevent leaks
                ticker.Stop();

                // Report error to user
                LblStatus.Foreground = Brushes.Red;
                LblStatus.Content = "Error";
            }
            finally
            {
                // Re-enable the test button regardless of outcome
                BtnTestPath.IsEnabled = true;
            }
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

    /// <summary>
    /// Contains extension methods for working with UI Automation elements.
    /// </summary>
    internal static class UiaExtensions
    {
        /// <summary>
        /// Gets a dictionary of attributes for the specified UI Automation element within the default timeout period of 5 seconds.
        /// </summary>
        /// <param name="element">The <see cref="IUIAutomationElement"/> to get the attributes of.</param>
        /// <returns>
        /// A dictionary of attribute names and values for the specified element, or an empty dictionary if the operation fails.
        /// </returns>
        public static IDictionary<string, string> GetAttributes(this IUIAutomationElement element)
        {
            // Call the overloaded GetAttributes method with a default timeout of 5 seconds.
            return GetAttributes(element, timeout: TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Gets a dictionary of attributes for the specified UI Automation element within the given timeout period.
        /// </summary>
        /// <param name="element">The <see cref="IUIAutomationElement"/> to get the attributes of.</param>
        /// <param name="timeout">The maximum amount of time to attempt to get the attributes.</param>
        /// <returns>
        /// A dictionary of attribute names and values for the specified element, or an empty dictionary if the operation fails.
        /// </returns>
        public static IDictionary<string, string> GetAttributes(this IUIAutomationElement element, TimeSpan timeout)
        {
            // Return an empty dictionary if the element is null.
            if (element == null)
            {
                return new Dictionary<string, string>();
            }

            // Formats the input string to be XML-safe by replacing special characters with their corresponding XML entities.
            static string FormatXml(string input)
            {
                // Check if the input string is null or empty.
                if (string.IsNullOrEmpty(input))
                {
                    // Return an empty string if the input is null or empty.
                    return string.Empty;
                }

                // Replace special characters with their corresponding XML entities and return the result.
                return input
                    .Replace("&", "&amp;")     // Ampersand
                    .Replace("\"", "&quot;")   // Double quote
                    .Replace("'", "&apos;")    // Single quote
                    .Replace("<", "&lt;")      // Less than
                    .Replace(">", "&gt;")      // Greater than
                    .Replace("\n", "&#xA;")    // Newline
                    .Replace("\r", "&#xD;");   // Carriage return
            }

            // Formats the attributes of the UI Automation element into a dictionary.
            static Dictionary<string, string> FormatAttributes(IUIAutomationElement info) => new(StringComparer.OrdinalIgnoreCase)
            {
                ["AcceleratorKey"] = FormatXml(info.CurrentAcceleratorKey),
                ["AccessKey"] = FormatXml(info.CurrentAccessKey),
                ["AriaProperties"] = FormatXml(info.CurrentAriaProperties),
                ["AriaRole"] = FormatXml(info.CurrentAriaRole),
                ["AutomationId"] = FormatXml(info.CurrentAutomationId),
                ["Bottom"] = $"{info.CurrentBoundingRectangle.bottom}",
                ["ClassName"] = FormatXml(info.CurrentClassName),
                ["FrameworkId"] = FormatXml(info.CurrentFrameworkId),
                ["HelpText"] = FormatXml(info.CurrentHelpText),
                ["IsContentElement"] = info.CurrentIsContentElement == 1 ? "true" : "false",
                ["IsControlElement"] = info.CurrentIsControlElement == 1 ? "true" : "false",
                ["IsEnabled"] = info.CurrentIsEnabled == 1 ? "true" : "false",
                ["IsKeyboardFocusable"] = info.CurrentIsKeyboardFocusable == 1 ? "true" : "false",
                ["IsPassword"] = info.CurrentIsPassword == 1 ? "true" : "false",
                ["IsRequiredForForm"] = info.CurrentIsRequiredForForm == 1 ? "true" : "false",
                ["ItemStatus"] = FormatXml(info.CurrentItemStatus),
                ["ItemType"] = FormatXml(info.CurrentItemType),
                ["Left"] = $"{info.CurrentBoundingRectangle.left}",
                ["Name"] = FormatXml(info.CurrentName),
                ["NativeWindowHandle"] = $"{info.CurrentNativeWindowHandle}",
                ["Orientation"] = $"{info.CurrentOrientation}",
                ["ProcessId"] = $"{info.CurrentProcessId}",
                ["Right"] = $"{info.CurrentBoundingRectangle.right}",
                ["Top"] = $"{info.CurrentBoundingRectangle.top}"
            };

            // Calculate the expiration time for the timeout.
            var expiration = DateTime.Now.Add(timeout);

            // Attempt to get the attributes until the timeout expires.
            while (DateTime.Now < expiration)
            {
                try
                {
                    // Format and return the attributes of the element.
                    return FormatAttributes(element);
                }
                catch (COMException)
                {
                    // Ignore COM exceptions and continue attempting until the timeout expires.
                }
            }

            // Return an empty dictionary if the attributes could not be retrieved within the timeout.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets a UI Automation element based on the specified xpath relative to the root element.
        /// </summary>
        /// <param name="automation">The UI Automation instance.</param>
        /// <param name="xpath">The xpath expression specifying the location of the element relative to the root element.</param>
        /// <returns>
        /// The UI Automation element found based on the xpath.
        /// Returns null if the xpath expression is invalid, does not specify criteria, or the element is not found.
        /// </returns>
        public static IUIAutomationElement GetElement(this CUIAutomation8 automation, string xpath)
        {
            // Normalize the xpath by removing any leading "/Desktop" segment.
            xpath = xpath.Replace("/Desktop", "/");
            xpath = xpath.StartsWith("///") ? xpath.Replace("///", "//") : xpath;

            // Get the root UI Automation element.
            var automationElement = automation.GetRootElement();

            // Use the FindElement method for finding an element based on the specified xpath.
            return FindElement(automationElement, xpath).Element?.UIAutomationElement;
        }

        /// <summary>
        /// Gets a UI Automation element based on the specified xpath relative to the current element.
        /// </summary>
        /// <param name="automationElement">The current UI Automation element.</param>
        /// <param name="xpath">The xpath expression specifying the location of the element relative to the current element.</param>
        /// <returns>
        /// The UI Automation element found based on the xpath.
        /// Returns null if the xpath expression is invalid, does not specify criteria, or the element is not found.
        /// </returns>
        public static IUIAutomationElement GetElement(this IUIAutomationElement automationElement, string xpath)
        {
            // Use the FindElement method for finding an element based on the specified xpath.
            return FindElement(automationElement, xpath).Element.UIAutomationElement;
        }

        /// <summary>
        /// Gets the tag name of the UI Automation element with a default timeout of 5 seconds.
        /// </summary>
        /// <param name="element">The <see cref="IUIAutomationElement"/> to get the tag name of.</param>
        /// <returns>The tag name of the element, or an empty string if the operation fails.</returns>
        public static string GetTagName(this IUIAutomationElement element)
        {
            // Call the overloaded GetTagName method with a default timeout of 5 seconds.
            return GetTagName(element, timeout: TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Gets the tag name of the UI Automation element within the specified timeout.
        /// </summary>
        /// <param name="element">The <see cref="IUIAutomationElement"/> to get the tag name of.</param>
        /// <param name="timeout">The maximum amount of time to attempt to get the tag name.</param>
        /// <returns>The tag name of the element, or an empty string if the operation fails.</returns>
        public static string GetTagName(this IUIAutomationElement element, TimeSpan timeout)
        {
            // Calculate the expiration time for the timeout.
            var expires = DateTime.Now.Add(timeout);

            // Attempt to get the tag name until the timeout expires.
            while (DateTime.Now < expires)
            {
                try
                {
                    // Get the control type field name corresponding to the element's current control type.
                    var controlType = typeof(UIA_ControlTypeIds).GetFields()
                        .Where(f => f.FieldType == typeof(int))
                        .FirstOrDefault(f => (int)f.GetValue(null) == element.CurrentControlType)?.Name;

                    // Extract and return the tag name from the control type field name.
                    return Regex.Match(input: controlType, pattern: "(?<=UIA_).*(?=ControlTypeId)").Value;
                }
                catch (COMException)
                {
                    // Ignore COM exceptions and continue attempting until the timeout expires.
                }
            }

            // Return an empty string if the tag name could not be retrieved within the timeout.
            return string.Empty;
        }

        /// <summary>
        /// Extracts element data from an <see cref="IUIAutomationElement"/>.
        /// </summary>
        /// <param name="element">The UI Automation element to extract data from.</param>
        /// <returns>An <see cref="ObservableCollection{ElementData}"/> containing element data.</returns>
        public static ObservableCollection<ElementDataModel> ExtractElementData(this IUIAutomationElement element)
        {
            // Get properties starting with "Current" from the UI Automation element type
            var attributes = element.GetAttributes();

            // Create a collection of ElementData from the properties
            var collection = attributes.Select(i => new ElementDataModel { Property = i.Key, Value = i.Value });

            // Return the element data collection as an ObservableCollection
            return new ObservableCollection<ElementDataModel>(collection);
        }

        // Finds a UI automation element based on the given XPath expression.
        private static (int Status, ElementModel Element) FindElement(IUIAutomationElement applicationRoot, string xpath)
        {
            // Converts an IUIAutomationElement to an Element.
            static ElementModel ConvertToElement(IUIAutomationElement automationElement)
            {
                // Generate a unique ID for the element based on the AutomationId, or use a new GUID if AutomationId is empty.
                var automationId = automationElement.CurrentAutomationId;
                var id = string.IsNullOrEmpty(automationId)
                    ? $"{Guid.NewGuid()}"
                    : automationElement.CurrentAutomationId;

                // Create a Location object based on the current bounding rectangle of the UI Automation element.
                var location = new LocationModel
                {
                    Bottom = automationElement.CurrentBoundingRectangle.bottom,
                    Left = automationElement.CurrentBoundingRectangle.left,
                    Right = automationElement.CurrentBoundingRectangle.right,
                    Top = automationElement.CurrentBoundingRectangle.top
                };

                // Create a new Element object and populate its properties.
                var element = new ElementModel
                {
                    Id = id,
                    UIAutomationElement = automationElement,
                    Location = location
                };

                // Return the created Element.
                return element;
            }

            // Convert the XPath expression to a UI Automation condition
            var condition = XpathParser.ConvertToCondition(xpath);

            // Return 400 status code if the XPath expression is invalid
            if (condition == null)
            {
                return (400, default);
            }

            // Determine the search scope based on the XPath expression
            var scope = xpath.StartsWith("//")
                ? TreeScope.TreeScope_Descendants
                : TreeScope.TreeScope_Children;

            // Find the first element that matches the condition within the specified scope
            var element = applicationRoot.FindFirst(scope, condition);

            // Return the status and element: 404 if not found, 200 if found
            return element == null
                ? (404, default)
                : (200, ConvertToElement(element));
        }
    }

    /// <summary>
    /// Factory class for creating a Document Object Model (DOM) representation of UI Automation elements.
    /// </summary>
    /// <param name="rootElement">The root UI Automation element.</param>
    internal class DocumentObjectModelFactory(IUIAutomationElement rootElement)
    {
        // The root UI Automation element used as the starting point for creating the DOM.
        private readonly IUIAutomationElement _rootElement = rootElement;

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentObjectModelFactory"/> class.
        /// </summary>
        public DocumentObjectModelFactory()
            : this(new CUIAutomation8().GetRootElement())
        { }

        /// <summary>
        /// Creates a new XML document representing the UI Automation element tree.
        /// </summary>
        /// <returns>A new <see cref="XDocument"/> representing the UI Automation element tree.</returns>
        public XDocument New()
        {
            // Create a new instance of the UI Automation object.
            var automation = new CUIAutomation8();

            // Use the root element if available; otherwise, get the desktop root element.
            var element = _rootElement ?? automation.GetRootElement();

            // Create the XML document from the UI Automation element tree.
            return New(automation, element, addDesktop: true);
        }

        /// <summary>
        /// Creates a new XML document representing the UI Automation element tree for the specified element.
        /// </summary>
        /// <param name="element">The UI Automation element.</param>
        /// <returns>A new <see cref="XDocument"/> representing the UI Automation element tree.</returns>
        public static XDocument New(IUIAutomationElement element)
        {
            // Create a new instance of the UI Automation object.
            var automation = new CUIAutomation8();

            // Create the XML document from the UI Automation element tree.
            return New(automation, element, addDesktop: true);
        }

        /// <summary>
        /// Creates a new XML document representing the UI Automation element tree for the specified automation object and element.
        /// </summary>
        /// <param name="automation">The UI Automation object.</param>
        /// <param name="element">The UI Automation element.</param>
        /// <returns>A new <see cref="XDocument"/> representing the UI Automation element tree.</returns>
        public static XDocument New(CUIAutomation8 automation, IUIAutomationElement element)
        {
            return New(automation, element, addDesktop: true);
        }

        /// <summary>
        /// Creates a new XML document representing the DOM structure of the given UI automation element.
        /// </summary>
        /// <param name="automation">The UI Automation instance to be used.</param>
        /// <param name="element">The UI Automation element to be processed.</param>
        /// <param name="addDesktop">Flag indicating whether to wrap the XML in a desktop tag.</param>
        /// <returns>An XDocument representing the XML structure of the UI element.</returns>
        public static XDocument New(CUIAutomation8 automation, IUIAutomationElement element, bool addDesktop)
        {
            // Register and generate XML data for the new DOM.
            var xmlData = Register(automation, element);

            // Construct the XML body with the tag name, attributes, and registered XML data.
            var xmlBody = string.Join("\n", xmlData);

            // Combine the XML data into a single XML string.
            var xml = addDesktop ? "<Desktop>" + xmlBody + "</Desktop>" : xmlBody;

            try
            {
                // Parse and return the XML document.
                return XDocument.Parse(xml);
            }
            catch (Exception e)
            {
                // Handle any parsing exceptions and return an error XML document.
                return XDocument.Parse($"<Desktop><Error>{e.GetBaseException().Message}</Error></Desktop>");
            }
        }

        // Registers and generates XML data for the new DOM.
        private static List<string> Register(CUIAutomation8 automation, IUIAutomationElement element)
        {
            // Initialize a list to store XML data.
            var xml = new List<string>();

            // Get the tag name and attributes of the element.
            var tagName = element.GetTagName();
            var attributes = GetElementAttributes(element);

            // Add the opening tag with attributes to the XML list.
            xml.Add($"<{tagName} {attributes}>");

            // Create a condition to find all child elements.
            var condition = automation.CreateTrueCondition();
            var treeWalker = automation.CreateTreeWalker(condition);
            var childElement = treeWalker.GetFirstChildElement(element);

            // Recursively process child elements.
            while (childElement != null)
            {
                var nodeXml = Register(automation, childElement);
                xml.AddRange(nodeXml);
                childElement = treeWalker.GetNextSiblingElement(childElement);
            }

            // Add the closing tag to the XML list.
            xml.Add($"</{tagName}>");

            // Return the complete XML data list.
            return xml;
        }

        // Gets the attributes of the specified UI Automation element as a string.
        private static string GetElementAttributes(IUIAutomationElement element)
        {
            // Get the attributes of the element.
            var attributes = element.GetAttributes();

            // Get the runtime ID of the element and serialize it to a JSON string.
            var runtime = element.GetRuntimeId().OfType<int>();
            var id = JsonSerializer.Serialize(runtime);
            attributes.Add("id", id);

            // Initialize a list to store attribute strings.
            var xmlNode = new List<string>();
            foreach (var item in attributes)
            {
                // Skip attributes with empty or whitespace-only keys or values.
                if (string.IsNullOrEmpty(item.Key) || string.IsNullOrEmpty(item.Value))
                {
                    continue;
                }

                // Add the attribute to the XML node list.
                xmlNode.Add($"{item.Key}=\"{item.Value}\"");
            }

            // Join the XML node representations into a single string and return it.
            return string.Join(" ", xmlNode);
        }
    }
}

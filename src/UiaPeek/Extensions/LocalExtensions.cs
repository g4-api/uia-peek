using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using UiaPeek.Models;

using UIAutomationClient;

namespace UiaPeek.Extensions
{
    /// <summary>
    /// Local extension methods for UI Automation.
    /// </summary>
    internal static class LocalExtensions
    {
        /// <summary>
        /// Converts an <see cref="IUIAutomationElement"/> into a <see cref="UiaNodeModel"/> representation.
        /// </summary>
        /// <param name="element">The UI Automation element to convert.</param>
        /// <returns>A <see cref="UiaNodeModel"/> containing extracted details such as name, control type, class name, automation ID, process ID, runtime ID, and bounding rectangle.</returns>
        public static UiaNodeModel ConvertToNode(this IUIAutomationElement element)
        {
            return Convert(element, metadata: false);
        }

        /// <summary>
        /// Builds a structured ancestor chain for a given UI Automation element.
        /// </summary>
        /// <param name="automation">The <see cref="CUIAutomation8"/> automation instance used for traversal.</param>
        /// <param name="element">The starting <see cref="IUIAutomationElement"/> from which to build the chain.</param>
        /// <returns>A <see cref="UiaChainModel"/> containing the ancestor path and the top-level window, or <c>null</c> if <paramref name="element"/> is <c>null</c>.</returns>
        public static UiaChainModel NewAncestorChain(this CUIAutomation8 automation, IUIAutomationElement element)
        {
            if (element == null)
            {
                // No element provided — cannot build a chain.
                return null;
            }

            // Collects ancestor nodes (including the starting element).
            var nodes = new List<UiaNodeModel>();

            // Tree walker used to navigate the UI Automation hierarchy.
            var walker = automation.RawViewWalker;

            // The desktop root element (absolute root of the UIA tree).
            var root = automation.GetRootElement();

            // Start traversal from the provided element.
            var current = element;

            // Flag to control whether to include full details (false) or metadata-only (true).
            // The starting element includes full details; ancestors only include metadata.
            var metadataOnly = false;

            // Tracks whether we are processing the first (starting) element.
            var isFirst = true;

            while (current != null)
            {
                // Convert the current element to a node model and add it to the chain.
                var node = Convert(current, metadataOnly);
                nodes.Add(node);

                // Mark the first element as the trigger element.
                if (isFirst)
                {
                    node.IsTriggerElement = true;
                    isFirst = false;
                }

                // Attempt to retrieve the parent element, handling COM issues safely.
                var parent = Safe(() => walker.GetParentElement(current), fallback: null);

                if (parent == null)
                {
                    // No parent found — this is the top of the chain.
                    break;
                }

                // Stop climbing further if the parent is the root desktop element.
                try
                {
                    if (automation.CompareElements(parent, root) == 1)
                    {
                        break;
                    }
                }
                catch (COMException)
                {
                    // If comparison fails due to a COM error, continue upward.
                }

                // After processing the first element, only metadata is needed for ancestors.
                metadataOnly = true;

                // Move upward in the hierarchy.
                current = parent;
            }

            // Reverse the collected nodes to have the trigger element last.
            nodes.Reverse();

            // Mark the top-level window in the chain.
            var topWindow = nodes.FirstOrDefault();

            // The top window is the first node in the reversed list.
            if (topWindow != null)
            {
                topWindow.IsTopWindow = true;
            }

            // Return the structured chain model.
            return new UiaChainModel
            {
                Path = nodes,
                TopWindow = topWindow
            };
        }

        /// <summary>
        /// Builds a UIA XPath-like locator string from a <see cref="UiaChainModel"/>.
        /// </summary>
        /// <param name="chain">The chain containing the ordered UIA nodes (trigger → ancestors).</param>
        /// <returns>A locator string beginning with <c>/Desktop</c> that walks the chain using <c>/</c> for contiguous ancestors and <c>//</c> when gaps are present.</returns>
        public static string ResolveLocator(this UiaChainModel chain)
        {
            // Local helper: identifiers are considered "broken" if they contain quotes (not safely embeddable).
            static bool IsBroken(string input)
                => input.Contains('\'') || input.Contains('"');

            // Use empty list if Path is null to avoid NRE; extension method assumes 'chain' itself is not null.
            var nodes = chain.Path ?? [];

            // Base of the locator always starts at Desktop.
            var xpathBuilder = new StringBuilder("/Desktop");

            // Tracks whether we skipped one or more intermediate nodes (causing '//' separator).
            var isGap = false;

            for (int i = 0; i < nodes.Count; i++)
            {
                // Current node and whether it's the last in the chain.
                var node = nodes[i];
                var isLast = (i == nodes.Count - 1);

                // Control type (fallback to '*' when unknown).
                var control = node.ControlType ?? "*";

                // Candidate identifiers.
                var name = node.Name;
                var automationId = node.AutomationId;

                // Determine presence and validity of identifiers.
                var hasName = !string.IsNullOrEmpty(name);
                var hasAutomationId = !string.IsNullOrEmpty(automationId);

                // Check if identifiers are "broken" (contain quotes).
                var nameBroken = hasName && IsBroken(name);
                var autoBroken = hasAutomationId && IsBroken(automationId);

                // LAST node with no identifiers -> emit //ControlType and finish.
                if (isLast && !hasName && !hasAutomationId)
                {
                    xpathBuilder.Append("//").Append(control);
                    break;
                }

                // Intermediate node with no identifiers -> skip and mark gap.
                if (!isLast && !hasName && !hasAutomationId)
                {
                    isGap = true;
                    continue;
                }

                // Choose separator: '//' if a gap was recorded, otherwise '/'.
                var sep = isGap ? "//" : "/";
                isGap = false;

                // Prefer Name if present and safe.
                if (hasName && !nameBroken)
                {
                    xpathBuilder
                        .Append(sep)
                        .Append(control)
                        .Append($"[@Name='{name}']");
                }
                // Else prefer AutomationId if present and safe.
                else if (hasAutomationId && !autoBroken)
                {
                    xpathBuilder
                        .Append(sep)
                        .Append(control)
                        .Append($"[@AutomationId='{automationId}']");
                }
                // Otherwise, identifiers exist but are broken (contain quotes) -> emit placeholder.
                else
                {
                    xpathBuilder
                        .Append(sep)
                        .Append(control)
                        .Append($"[@Name='~BROKEN~']");
                }
            }

            // Return the constructed XPath-like locator string.
            return xpathBuilder.ToString();
        }

        // Converts an IUIAutomationElement into a UiaNodeModel representation.
        private static UiaNodeModel Convert(IUIAutomationElement element, bool metadata)
        {
            // Extract common properties from the UIA element
            var automationId = Safe(() => element.CurrentAutomationId);
            var className = Safe(() => element.CurrentClassName);
            var controlTypeId = Safe(() => element.CurrentControlType);
            var name = Safe(() => element.CurrentName);
            var pid = Safe(() => element.CurrentProcessId);

            // Resolve the control type name using the cache, defaulting to "*"
            var controlType = Cache.ControlTypeNames.GetValueOrDefault(
                key: controlTypeId,
                defaultValue: "*"
            );

            // If only metadata is requested, return a simplified model
            if (metadata)
            {
                return new UiaNodeModel
                {
                    AutomationId = string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                    ClassName = string.IsNullOrWhiteSpace(className) ? null : className,
                    ControlType = string.IsNullOrWhiteSpace(controlType) ? null : controlType,
                    ControlTypeId = controlTypeId,
                    Name = string.IsNullOrWhiteSpace(name) ? null : name,
                    ProcessId = pid
                };
            }

            // Initialize runtimeId to an empty array
            int[] runtimeId = [];

            try
            {
                // Attempt to get the runtime ID (may fail for certain elements)
                runtimeId = (int[])element.GetRuntimeId() ?? [];
            }
            catch
            {
                // Ignore errors when retrieving runtime ID
            }

            // Extract the element bounding rectangle
            var rectangle = Safe(() => element.CurrentBoundingRectangle);

            // Extract supported patterns (if any)
            var patterns = GetPatterns(element);

            // Extract mchine information
            var machine = new UiaNodeModel.MachineDataModel
            {
                Name = Environment.MachineName,
                PublicAddress = ControllerUtilities.GetLocalEndpoint()
            };

            // Build and return the node model
            return new UiaNodeModel
            {
                AutomationId = string.IsNullOrWhiteSpace(automationId) ? null : automationId,
                Bounds = new UiaNodeModel.BoundsRectangle
                {
                    Left = rectangle.left,
                    Top = rectangle.top,
                    Width = Math.Max(0, rectangle.right - rectangle.left),
                    Height = Math.Max(0, rectangle.bottom - rectangle.top)
                },
                ClassName = string.IsNullOrWhiteSpace(className) ? null : className,
                ControlType = string.IsNullOrWhiteSpace(controlType) ? null : controlType,
                ControlTypeId = controlTypeId,
                Machine = machine,
                Name = string.IsNullOrWhiteSpace(name) ? null : name,
                Patterns = [.. patterns],
                ProcessId = pid,
                RuntimeId = runtimeId.Length > 0 ? runtimeId : null
            };
        }

        // Retrieves the list of supported UI Automation patterns for a given element.
        private static List<UiaNodeModel.PatternDataModel> GetPatterns(IUIAutomationElement element)
        {
            // Resolves all supported UI Automation patterns for a given element.
            static List<UiaNodeModel.PatternDataModel> ResolvePatterns(IUIAutomationElement element)
            {
                // Holds all supported pattern metadata for the element.
                var list = new List<UiaNodeModel.PatternDataModel>();

                // Iterate through all known UI Automation pattern IDs and names.
                foreach (var (id, name) in Cache.PatternNames)
                {
                    // Attempt to retrieve the current pattern for this ID.
                    // Uses Safe<T> to handle COM-related exceptions gracefully.
                    var patternObj = Safe(() => element.GetCurrentPattern(id), fallback: null);

                    // If the pattern is not supported, skip to the next.
                    if (patternObj == null)
                    {
                        continue;
                    }

                    // Add the supported pattern metadata to the result list.
                    list.Add(new UiaNodeModel.PatternDataModel
                    {
                        Id = id,
                        Name = name
                    });
                }

                // Return all supported patterns for the element.
                return list;
            }

            // Initialize an empty list to hold pattern data.
            var list = new List<UiaNodeModel.PatternDataModel>();

            try
            {
                // Attempt to resolve supported patterns.
                return ResolvePatterns(element);
            }
            catch (COMException)
            {
                // Ignore; element may be stale or provider buggy.
            }
            catch (InvalidComObjectException)
            {
                // Ignore; element may have been released.
            }

            // Return an empty list if exceptions occurred.
            return list;
        }

        // Safely executes a function that retrieves a COM-related value,
        // returning a fallback value if a known exception occurs.
        private static T Safe<T>(Func<T> getter, T fallback = default)
        {
            try
            {
                // Attempt to execute the provided function.
                return getter();
            }
            catch (COMException)
            {
                // COM object is not available or failed; return fallback.
                return fallback;
            }
            catch (NullReferenceException)
            {
                // Getter referenced a null object; return fallback.
                return fallback;
            }
            catch (InvalidComObjectException)
            {
                // The COM object has been released or is invalid; return fallback.
                return fallback;
            }
        }
    }
}

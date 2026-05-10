using Common.Domain.Extensions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using UiaPeek.Domain.Models;

using UIAutomationClient;

namespace UiaPeek.Domain.Extensions
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

                // Calculate sibling indexes while both current element and parent are available.
                var (siblingIdx, sameTypeIdx) = GetSiblingIndexes(walker, automation, parent, current, node.ControlTypeId);
                node.SiblingIndex = siblingIdx;
                node.SiblingIndexOfSameControlType = sameTypeIdx;

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
            topWindow?.IsTopWindow = true;

            // Return the structured chain model.
            return new UiaChainModel
            {
                Path = nodes,
                TopWindow = topWindow
            };
        }

        /// <summary>
        /// Builds a deterministic UIA XPath-like locator string from a <see cref="UiaChainModel"/>.
        /// Every ancestor node is included in the path using a single <c>/</c> separator.
        /// Nodes are identified by <c>AutomationId</c> or <c>Name</c> when safe, otherwise by
        /// a 1-based sibling index among nodes of the same <c>ControlType</c>.
        /// UWP <c>Windows.UI.Core.CoreWindow</c> nodes are omitted because UIA cannot resolve them.
        /// </summary>
        /// <param name="chain">The chain containing the ordered UIA ancestor nodes.</param>
        /// <returns>A deterministic locator string beginning with <c>/Desktop</c>.</returns>
        public static string ResolveLocator(this UiaChainModel chain)
        {
            // Identifiers containing quotes cannot be safely embedded in XPath attribute predicates.
            static bool IsBroken(string input) => input.Contains('\'') || input.Contains('"');

            var nodes = chain?.Path ?? [];
            var builder = new StringBuilder("/Desktop");

            foreach (var node in nodes)
            {
                // Control type label, falling back to '*' when the type is unknown.
                var control = node.ControlType ?? "*";

                // UWP CoreWindow elements are invisible to UIA — omit from the path entirely.
                // The adjacent nodes connect with '/' as if the UWP layer does not exist.
                var isUwp = node.ClassName?.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) == true;
                if (isUwp)
                {
                    continue;
                }

                var automationId = node.AutomationId;
                var name = node.Name;

                var hasAutomationId = !string.IsNullOrEmpty(automationId) && !IsBroken(automationId);
                var hasName = !string.IsNullOrEmpty(name) && !IsBroken(name);

                if (hasAutomationId)
                {
                    // Strong identifier — use AutomationId predicate.
                    builder.Append('/').Append(control).Append($"[@AutomationId='{automationId}']");
                }
                else if (hasName)
                {
                    // Secondary identifier — use Name predicate.
                    builder.Append('/').Append(control).Append($"[@Name='{name}']");
                }
                else
                {
                    // No usable identifier — disambiguate with 1-based index among same-ControlType siblings.
                    builder.Append('/').Append(control).Append($"[{node.SiblingIndexOfSameControlType}]");
                }
            }

            return builder.ToString();
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
                Element = element,
                FrameworkId = Safe(() => element.CurrentFrameworkId),
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

        // Calculates 1-based sibling indexes for a UIA element among its parent's children.
        // Walks siblings via RawViewWalker from the first child until the target is found.
        // Returns (index among all siblings, index among siblings with the same ControlTypeId).
        // Falls back to (1, 1) if enumeration fails or the target is not found.
        private static (int All, int SameControlType) GetSiblingIndexes(
            IUIAutomationTreeWalker walker,
            CUIAutomation8 automation,
            IUIAutomationElement parent,
            IUIAutomationElement target,
            int targetControlTypeId)
        {
            // Count predecessors (0-based); add 1 at the return point to produce 1-based XPath positions.
            var allCount = 0;
            var sameTypeCount = 0;

            try
            {
                var child = Safe(() => walker.GetFirstChildElement(parent), fallback: null);

                while (child != null)
                {
                    // Check whether this sibling is the target element.
                    var isTarget = false;

                    try
                    {
                        isTarget = automation.CompareElements(child, target) == 1;
                    }
                    catch (COMException) { }
                    catch (InvalidComObjectException) { }

                    if (isTarget)
                    {
                        break;
                    }

                    allCount++;

                    if (Safe(() => child.CurrentControlType) == targetControlTypeId)
                    {
                        sameTypeCount++;
                    }

                    child = Safe(() => walker.GetNextSiblingElement(child), fallback: null);
                }
            }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
            catch (Exception) { }

            // +1 converts predecessor count (0-based) to 1-based XPath sibling position.
            return (allCount + 1, sameTypeCount + 1);
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
            catch (InvalidComObjectException)
            {
                // The COM object has been released or is invalid; return fallback.
                return fallback;
            }
            catch (Exception)
            {
                // Getter referenced a null object; return fallback.
                return fallback;
            }
        }
    }
}

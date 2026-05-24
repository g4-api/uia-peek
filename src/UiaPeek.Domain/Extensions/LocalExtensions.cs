using Common.Domain.Extensions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

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
            // Extract the ancestor nodes from the chain, defaulting to an empty list if the chain is null.
            var nodes = chain?.Path ?? [];
            var builder = new StringBuilder("/Desktop");

            // Set to true when a UWP CoreWindow node is skipped; causes the next node to use '//'
            // because UIA cannot step through the UWP layer with a single '/'.
            var isGap = false;

            foreach (var node in nodes)
            {
                // Control type label, falling back to '*' when the type is unknown.
                var control = node.ControlType ?? "*";

                // UWP CoreWindow elements are invisible to UIA — omit from the path but mark a gap
                // so the following node uses '//' to bridge the unreachable UWP layer.
                var isUwp = node.ClassName?.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) == true;
                if (isUwp)
                {
                    isGap = true;
                    continue;
                }

                // '//' when bridging a UWP gap, '/' for every other step.
                var separator = isGap ? "//" : "/";
                isGap = false;

                // Attempt to use AutomationId as the strongest available identifier, falling back to Name when safe,
                // otherwise using a positional index among siblings of the same ControlType.
                var automationId = node.AutomationId;
                var name = node.Name;

                // Identifiers that contain quotes cannot be used in XPath predicates and are considered broken.
                var hasAutomationId = !string.IsNullOrEmpty(automationId) && !IsBroken(automationId);
                var hasName = !string.IsNullOrEmpty(name) && !IsBroken(name);

                if (hasAutomationId)
                {
                    // Strong identifier — use AutomationId predicate.
                    builder.Append(separator).Append(control).Append($"[@AutomationId='{automationId}']");
                }
                else if (hasName)
                {
                    // Secondary identifier — use Name predicate.
                    builder.Append(separator).Append(control).Append($"[@Name='{name}']");
                }
                else
                {
                    // No usable identifier — disambiguate with 1-based index among same-ControlType siblings.
                    builder.Append(separator).Append(control).Append($"[{node.SiblingIndexOfSameControlType}]");
                }
            }

            // Return the constructed locator string.
            return builder.ToString();

            // Identifiers containing quotes cannot be safely embedded in XPath attribute predicates.
            static bool IsBroken(string input) => input.Contains('\'') || input.Contains('"');
        }

        /// <summary>
        /// Builds a normalized XPath locator from the UIA chain fallback locator by removing
        /// known structural wrapper segments while preserving the original locator semantics.
        /// </summary>
        /// <param name="chain">The UIA chain model that contains the fallback XPath locator.</param>
        /// <returns>A normalized XPath when removable wrapper segments were found and safely removed; otherwise, <c>null</c>.</returns>
        public static string FormatXpath(this UiaChainModel chain)
        {
            // Get the complete fallback XPath generated from the UIA chain.
            var xpath = chain?.FallbackLocator;

            // Guard against null, empty, whitespace, or non-absolute XPath values.
            // A valid UIA XPath should start from the root using '/'.
            if (string.IsNullOrWhiteSpace(xpath) || !xpath.StartsWith('/'))
            {
                return null;
            }

            // Split the XPath into ordered segments while preserving the separator
            // that was used before each segment.
            //
            // Example:
            // /Window[1]/Panel[1]//Button[@AutomationId='x']
            //
            // Captures:
            // Sep="/"  Text="Window[1]"
            // Sep="/"  Text="Panel[1]"
            // Sep="//" Text="Button[@AutomationId='x']"
            var matches = Regex.Matches(xpath, @"(/+)([^/]+)");

            // A meaningful normalized locator needs at least a root segment and a target segment.
            if (matches.Count < 2)
            {
                return null;
            }

            // Convert regex matches into a simple array so each segment can be inspected,
            // marked as removable, and later rebuilt in the original order.
            var segments = matches
                .Select(m => (Sep: m.Groups[1].Value, Text: m.Groups[2].Value))
                .ToArray();

            // The last segment is the target element.
            // It must never be removed, and it must be meaningful enough to justify
            // generating a normalized locator.
            if (!AssertMeaningfulSegment(segments[^1].Text))
            {
                return null;
            }

            // Track which intermediate segments can be removed.
            // The first segment is preserved as the root boundary.
            // The final segment is preserved as the target element.
            var removable = new bool[segments.Length];

            // Mark intermediate segments that represent known structural wrappers and can be safely removed.
            for (var i = 1; i < segments.Length - 1; i++)
            {
                removable[i] = AssertRemovableWrapper(segments[i].Text);
            }

            // If no known wrapper segments were found, there is nothing to normalize.
            if (!Array.Exists(removable, r => r))
            {
                return null;
            }

            // Rebuild the XPath by skipping removable segments and joining
            // the kept segments with the appropriate separator.
            var stringBuilder = new StringBuilder();

            // Indicates that one or more segments were skipped.
            // When true, the next kept segment must be connected using '//'
            // because the direct parent-child relationship is no longer guaranteed.
            var isGap = false;

            for (var i = 0; i < segments.Length; i++)
            {
                // Skip removable wrapper segments and remember that a hierarchy gap exists.
                if (removable[i])
                {
                    isGap = true;
                    continue;
                }

                if (i == 0)
                {
                    // Always write the first/root segment with a single leading slash.
                    stringBuilder.Append('/').Append(segments[i].Text);
                }
                else
                {
                    // Use descendant navigation when:
                    // 1. one or more segments were skipped, or
                    // 2. the original XPath already used descendant navigation.
                    stringBuilder.Append(isGap || segments[i].Sep == "//" ? "//" : "/");
                    stringBuilder.Append(segments[i].Text);

                    // The gap has been consumed by the current kept segment.
                    isGap = false;
                }
            }

            // Get the final normalized XPath string.
            // This may be the same as the original if no segments were removed, but that case is handled by an early return above.
            var result = stringBuilder.ToString();

            // Return null when normalization did not actually change the locator.
            return result == xpath ? null : result;

            // Determines whether the specified XPath segment represents a structural UIA wrapper
            // that can be safely removed from a normalized locator.
            static bool AssertRemovableWrapper(string segment)
            {
                // Find the start of the XPath predicate.
                // Example: "Panel[1]" -> bracketIndex points to '['.
                var bracketIndex = segment.IndexOf('[');

                // Segments without predicates cannot be treated as index-only wrappers.
                if (bracketIndex < 0)
                {
                    return false;
                }

                // Extract the control type before the predicate.
                // Example: "Panel[1]" -> "Panel".
                var controlType = segment[..bracketIndex];

                // Extract the predicate content without the surrounding brackets.
                // Example: "Panel[1]" -> "1".
                var predicate = segment[(bracketIndex + 1)..^1];

                // Windows 11 host/boundary wrapper.
                // It is safe to remove only when it has no semantic predicate.
                if (controlType.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
                {
                    return AssertIndexOnly(predicate);
                }

                // Windows UIA structural wrappers.
                // Pane is common on Windows 11; Panel can appear in Windows 10 paths,
                // for example in Calculator as /Panel[i]/Panel[i].
                if (controlType is "Pane" or "Panel")
                {
                    return AssertIndexOnly(predicate);
                }

                // Any other control type is not considered removable by this rule.
                return false;
            }

            // Returns true when a segment qualifies as a meaningful locator target.
            // A segment is meaningful when it carries an attribute predicate (@AutomationId, @Name, ...)
            // or belongs to a clearly semantic UIA control type.
            static bool AssertMeaningfulSegment(string segment)
            {
                // Known UIA control types that usually represent real interaction targets
                // or meaningful UI content, even when they only have an index predicate.
                var semanticControlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Button",
                    "Edit",
                    "Text",
                    "MenuItem",
                    "ListItem",
                    "ComboBox",
                    "CheckBox",
                    "RadioButton",
                    "TabItem",
                    "Hyperlink",
                    "DataItem",
                    "TreeItem"
                };

                // Find the start of the XPath predicate.
                // Example: "Button[@AutomationId='num7Button']" -> bracketIdx points to '['.
                var bracketIdx = segment.IndexOf('[');

                // Extract the control type.
                // Example: "Button[@AutomationId='num7Button']" -> "Button".
                // Example: "Button" -> "Button".
                var controlType = bracketIdx < 0 ? segment : segment[..bracketIdx];

                // Extract the predicate without the surrounding brackets.
                // Example: "Button[@AutomationId='num7Button']" -> "@AutomationId='num7Button'".
                // If there is no predicate, keep it as null.
                var predicate = bracketIdx < 0 ? null : segment[(bracketIdx + 1)..^1];

                // A segment is meaningful when it has an attribute-based predicate,
                // for example @AutomationId, @Name, @ClassName, etc.
                // It is also meaningful when its control type is one of the known
                // semantic UIA control types listed above.
                return (predicate != null && predicate.StartsWith('@'))
                    || semanticControlTypes.Contains(controlType);
            }

            // Predicate is index-only (e.g. "1", "2") — contains no attribute selector.
            static bool AssertIndexOnly(string predicate) => int.TryParse(predicate, out _);
        }

        // TODO: Export all properties that can be safely retrieved from the element, such as IsContentElement, IsControlElement, IsEnabled, etc.
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
                // Start with the first child of the parent element.
                // If the parent has no children or retrieval fails, this will be null and the loop will be skipped.
                var child = Safe(() => walker.GetFirstChildElement(parent), fallback: null);

                // Walk through siblings until the target element is found, counting positions.
                while (child != null)
                {
                    // Check whether this sibling is the target element.
                    var isTarget = false;

                    // CompareElements can throw if either element is stale or the provider
                    // is buggy, so we catch exceptions and treat them as non-matches.
                    try
                    {
                        isTarget = automation.CompareElements(child, target) == 1;
                    }
                    catch (COMException) { }
                    catch (InvalidComObjectException) { }

                    // If this sibling is the target, stop counting; otherwise,
                    // increment counts and move to the next sibling.
                    if (isTarget)
                    {
                        break;
                    }

                    // Increment the count of all siblings encountered so far.
                    // This count is used to determine the target's position among all siblings.
                    allCount++;

                    // If this sibling shares the same ControlTypeId as the target, increment the same-type count.
                    if (Safe(() => child.CurrentControlType) == targetControlTypeId)
                    {
                        sameTypeCount++;
                    }

                    // Move to the next sibling element, handling potential COM exceptions safely.
                    // If retrieval fails, child will be set to null and the loop will exit.
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

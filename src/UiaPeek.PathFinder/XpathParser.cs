using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using UIAutomationClient;

namespace UiaPeek.PathFinder
{
    /// <summary>
    /// Provides methods to parse XPath strings and convert them to UI Automation conditions.
    /// </summary>
    public static class XpathParser
    {
        // The UI Automation instance
        private static readonly CUIAutomation8 s_automation = new();

        // The string comparer for case-insensitive comparisons
        private static readonly StringComparer s_comparer = StringComparer.OrdinalIgnoreCase;

        // Mapping of control types to UI Automation control type IDs
        private static readonly Dictionary<string, int> s_controlTypeMapping = typeof(UIA_ControlTypeIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(
                k => Regex.Match(k.Name, "(?<=UIA_)\\w+(?=ControlTypeId)").Value,
                v => (int)v.GetValue(null), s_comparer);

        // Mapping of property names to UI Automation property IDs
        private static readonly Dictionary<string, int> s_propertyIdMapping = typeof(UIA_PropertyIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(
                k => Regex.Match(k.Name, "(?<=UIA_)\\w+(?=PropertyId)").Value,
                v => (int)v.GetValue(null), s_comparer);

        /// <summary>
        /// Converts an XPath string to a UI Automation condition.
        /// </summary>
        /// <param name="xpath">The XPath string to convert.</param>
        /// <returns>An <see cref="IUIAutomationCondition"/> representing the condition tree constructed from the XPath string.</returns>
        public static IUIAutomationCondition ConvertToCondition(string xpath)
        {
            // Format the XPath string into segments
            var segments = FormatXpath(xpath);

            // Create a new condition tree from the formatted segments
            return NewConditionTree(segments);
        }

        // Formats the given XPath string by extracting control types, logical operators, parentheses, and conditions.
        private static List<string> FormatXpath(string xpath)
        {
            // Define the regex pattern to match control types, logical operators, parentheses, and conditions
            const string pattern = @"(?<controlType>/{0,2}\w+)|(?<logical>\band\b|\bor\b|\bnot\b)|(?<parentheses>[\(\)])|(?<condition>@[\w\-]+='[^']*')";

            // Find matches in the xpath string based on the defined pattern
            var matches = Regex.Matches(xpath, pattern);

            // Convert the matches to a list of strings, trim whitespace, and filter out empty tokens
            return [.. matches.Cast<Match>()
                .Select(match => match.Value.Trim(' ', '/'))
                .Where(token => !string.IsNullOrEmpty(token))];
        }

        // Creates a new UI Automation condition tree based on the specified segments.
        private static IUIAutomationCondition NewConditionTree(List<string> segments)
        {
            // Create stacks for conditions
            var conditionStack = new Stack<IUIAutomationCondition>();

            // Create a stack for operators
            var operatorStack = new Stack<string>();

            // Initialize the control type condition
            IUIAutomationCondition controlTypeCondition = null;

            // Create a regular expression pattern for logical operators (and, or, not) in a case-insensitive manner
            var logicalOperatorPattern = new Regex("(?is)^(and|or|not)$");

            // Process each segment in the list of segments from the XPath expression string
            foreach (var segment in segments)
            {
                // Check if the segment is a control type
                if (s_controlTypeMapping.ContainsKey(segment))
                {
                    // Create a control type condition
                    controlTypeCondition = NewControlTypeCondition(segment);
                }
                // Check if the segment is a logical operator (and, or, not)
                else if (logicalOperatorPattern.IsMatch(input: segment))
                {
                    operatorStack.Push(segment);
                }
                // Check if the segment is an opening parenthesis
                else if (segment.Equals("(", StringComparison.OrdinalIgnoreCase))
                {
                    operatorStack.Push(segment);
                }
                // Check if the segment is a closing parenthesis
                else if (segment.Equals(")", StringComparison.OrdinalIgnoreCase))
                {
                    while (operatorStack.Peek() != "(")
                    {
                        // Create a new condition based on the operator and push it to the condition stack
                        var condition = NewOperatorCondition(operatorStack.Pop(), conditionStack);

                        // Push the condition to the condition stack
                        conditionStack.Push(condition);
                    }

                    // Pop the opening parenthesis from the operator stack
                    operatorStack.Pop();
                }
                // Check if the segment is a property condition
                else if (segment.StartsWith('@'))
                {
                    // Create a property condition and push it to the condition stack
                    conditionStack.Push(NewPropertyCondition(segment));
                }
            }

            // Process remaining operators in the operator stack
            while (operatorStack.Count > 0)
            {
                // Create a new condition based on the operator
                var condition = NewOperatorCondition(operatorStack.Pop(), conditionStack);

                // Push the condition to the condition stack
                conditionStack.Push(condition);
            }

            // Combine the control type condition with the condition stack if necessary
            if (controlTypeCondition != null && conditionStack.Count > 0)
            {
                // Createa combined condition based on the control type condition and the top condition in the stack
                var combinedCondition = s_automation.CreateAndCondition(controlTypeCondition, conditionStack.Pop());

                // Push the combined condition to the condition stack
                conditionStack.Push(combinedCondition);
            }

            // Return the final condition from the condition stack or the control type condition if the stack is empty
            return conditionStack.Count > 0 ? conditionStack.Pop() : controlTypeCondition;
        }

        // Creates a new UI Automation condition for the specified control type.
        private static IUIAutomationCondition NewControlTypeCondition(string controlType)
        {
            // Check if the control type is mapped to a control type ID
            var isId = s_controlTypeMapping.TryGetValue(key: controlType, out int id);

            // Throw an exception if the control type is not supported
            if (!isId)
            {
                throw new NotSupportedException($"Unsupported control type: {controlType}");
            }

            // Create and return the property condition for the control type
            return s_automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, id);
        }

        // Creates a new UI Automation condition based on a logical operator and a stack of conditions.
        private static IUIAutomationCondition NewOperatorCondition(string logicalOperator, Stack<IUIAutomationCondition> conditionStack)
        {
            // Check for the logical operator and create the corresponding condition
            if (logicalOperator.Equals("and", StringComparison.OrdinalIgnoreCase))
            {
                // Pop two conditions from the stack and create an AND condition
                var right = conditionStack.Pop();
                var left = conditionStack.Pop();

                // Create and return the AND condition
                return s_automation.CreateAndCondition(left, right);
            }
            else if (logicalOperator.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                // Pop two conditions from the stack and create an OR condition
                var right = conditionStack.Pop();
                var left = conditionStack.Pop();

                // Create and return the OR condition
                return s_automation.CreateOrCondition(left, right);
            }
            else if (logicalOperator.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                // Pop one condition from the stack and create a NOT condition
                var condition = conditionStack.Pop();

                // Create and return the NOT condition
                return s_automation.CreateNotCondition(condition);
            }

            // Throw an exception if the logical operator is not supported
            throw new NotSupportedException($"Unsupported operator: {logicalOperator}");
        }

        // Creates a new UI Automation condition for properties based on the specified segment.
        private static IUIAutomationCondition NewPropertyCondition(string segment)
        {
            // Remove the leading character and split the segment into property name and value
            var parts = segment[1..].Split('=');
            var propertyName = parts[0].Trim();
            var value = parts[1].Trim('\'', '\"');

            // Determine if the condition should match a substring
            var conditionFlag = propertyName.StartsWith("partial", StringComparison.OrdinalIgnoreCase)
                ? PropertyConditionFlags.PropertyConditionFlags_MatchSubstring
                : PropertyConditionFlags.PropertyConditionFlags_None;

            // Remove "partial" from the property name if present
            propertyName = Regex.Replace(input: propertyName, pattern: "(?is)^partial", replacement: string.Empty);

            // Check if the property name is mapped to a property ID
            var isId = s_propertyIdMapping.TryGetValue(key: propertyName, out int id);

            // Throw an exception if the property is not supported
            if (!isId)
            {
                throw new NotSupportedException($"Unsupported property: {propertyName}");
            }

            // Create and return the property condition with the specified flags
            return s_automation.CreatePropertyConditionEx(id, value, conditionFlag);
        }
    }
}

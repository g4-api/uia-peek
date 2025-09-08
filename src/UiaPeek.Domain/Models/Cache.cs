using System.Collections.Generic;

using UIAutomationClient;

namespace UiaPeek.Domain.Models
{
    public static class Cache
    {
        /// <summary>
        /// Maps UI Automation control type IDs to their corresponding friendly names.
        /// </summary>
        public static Dictionary<int, string> ControlTypeNames => new()
        {
            { UIA_ControlTypeIds.UIA_AppBarControlTypeId, "AppBar" },
            { UIA_ControlTypeIds.UIA_ButtonControlTypeId, "Button" },
            { UIA_ControlTypeIds.UIA_CalendarControlTypeId, "Calendar" },
            { UIA_ControlTypeIds.UIA_CheckBoxControlTypeId, "CheckBox" },
            { UIA_ControlTypeIds.UIA_ComboBoxControlTypeId, "ComboBox" },
            { UIA_ControlTypeIds.UIA_CustomControlTypeId, "Custom" },
            { UIA_ControlTypeIds.UIA_DataGridControlTypeId, "DataGrid" },
            { UIA_ControlTypeIds.UIA_DataItemControlTypeId, "DataItem" },
            { UIA_ControlTypeIds.UIA_DocumentControlTypeId, "Document" },
            { UIA_ControlTypeIds.UIA_EditControlTypeId, "Edit" },
            { UIA_ControlTypeIds.UIA_GroupControlTypeId, "Group" },
            { UIA_ControlTypeIds.UIA_HeaderControlTypeId, "Header" },
            { UIA_ControlTypeIds.UIA_HeaderItemControlTypeId, "HeaderItem" },
            { UIA_ControlTypeIds.UIA_HyperlinkControlTypeId, "Hyperlink" },
            { UIA_ControlTypeIds.UIA_ImageControlTypeId, "Image" },
            { UIA_ControlTypeIds.UIA_ListControlTypeId, "List" },
            { UIA_ControlTypeIds.UIA_ListItemControlTypeId, "ListItem" },
            { UIA_ControlTypeIds.UIA_MenuControlTypeId, "Menu" },
            { UIA_ControlTypeIds.UIA_MenuBarControlTypeId, "MenuBar" },
            { UIA_ControlTypeIds.UIA_MenuItemControlTypeId, "MenuItem" },
            { UIA_ControlTypeIds.UIA_PaneControlTypeId, "Pane" },
            { UIA_ControlTypeIds.UIA_ProgressBarControlTypeId, "ProgressBar" },
            { UIA_ControlTypeIds.UIA_RadioButtonControlTypeId, "RadioButton" },
            { UIA_ControlTypeIds.UIA_ScrollBarControlTypeId, "ScrollBar" },
            { UIA_ControlTypeIds.UIA_SeparatorControlTypeId, "Separator" },
            { UIA_ControlTypeIds.UIA_SemanticZoomControlTypeId, "SemanticZoom" },
            { UIA_ControlTypeIds.UIA_SliderControlTypeId, "Slider" },
            { UIA_ControlTypeIds.UIA_SpinnerControlTypeId, "Spinner" },
            { UIA_ControlTypeIds.UIA_SplitButtonControlTypeId, "SplitButton" },
            { UIA_ControlTypeIds.UIA_StatusBarControlTypeId, "StatusBar" },
            { UIA_ControlTypeIds.UIA_TabControlTypeId, "Tab" },
            { UIA_ControlTypeIds.UIA_TabItemControlTypeId, "TabItem" },
            { UIA_ControlTypeIds.UIA_TableControlTypeId, "Table" },
            { UIA_ControlTypeIds.UIA_TextControlTypeId, "Text" },
            { UIA_ControlTypeIds.UIA_ThumbControlTypeId, "Thumb" },
            { UIA_ControlTypeIds.UIA_TitleBarControlTypeId, "TitleBar" },
            { UIA_ControlTypeIds.UIA_ToolBarControlTypeId, "ToolBar" },
            { UIA_ControlTypeIds.UIA_ToolTipControlTypeId, "ToolTip" },
            { UIA_ControlTypeIds.UIA_TreeControlTypeId, "Tree" },
            { UIA_ControlTypeIds.UIA_TreeItemControlTypeId, "TreeItem" },
            { UIA_ControlTypeIds.UIA_WindowControlTypeId, "Window" },
        };

        /// <summary>
        /// Maps UI Automation pattern IDs to their corresponding friendly names.
        /// </summary>
        public static Dictionary<int, string> PatternNames => new()
        {
            { UIA_PatternIds.UIA_AnnotationPatternId, "Annotation" },
            { UIA_PatternIds.UIA_DockPatternId, "Dock" },
            { UIA_PatternIds.UIA_DragPatternId, "Drag" },
            { UIA_PatternIds.UIA_DropTargetPatternId, "DropTarget" },
            { UIA_PatternIds.UIA_ExpandCollapsePatternId, "ExpandCollapse" },
            { UIA_PatternIds.UIA_GridPatternId, "Grid" },
            { UIA_PatternIds.UIA_GridItemPatternId, "GridItem" },
            { UIA_PatternIds.UIA_InvokePatternId, "Invoke" },
            { UIA_PatternIds.UIA_ItemContainerPatternId, "ItemContainer" },
            { UIA_PatternIds.UIA_LegacyIAccessiblePatternId, "LegacyIAccessible" },
            { UIA_PatternIds.UIA_MultipleViewPatternId, "MultipleView" },
            { UIA_PatternIds.UIA_ObjectModelPatternId, "ObjectModel" },
            { UIA_PatternIds.UIA_RangeValuePatternId, "RangeValue" },
            { UIA_PatternIds.UIA_ScrollPatternId, "Scroll" },
            { UIA_PatternIds.UIA_ScrollItemPatternId, "ScrollItem" },
            { UIA_PatternIds.UIA_SelectionPatternId, "Selection" },
            { UIA_PatternIds.UIA_SelectionItemPatternId, "SelectionItem" },
            { UIA_PatternIds.UIA_SpreadsheetPatternId, "Spreadsheet" },
            { UIA_PatternIds.UIA_SpreadsheetItemPatternId, "SpreadsheetItem" },
            { UIA_PatternIds.UIA_StylesPatternId, "Styles" },
            { UIA_PatternIds.UIA_SynchronizedInputPatternId, "SynchronizedInput" },
            { UIA_PatternIds.UIA_TablePatternId, "Table" },
            { UIA_PatternIds.UIA_TableItemPatternId, "TableItem" },
            { UIA_PatternIds.UIA_TextPatternId, "Text" },
            { UIA_PatternIds.UIA_TextPattern2Id, "Text2" },
            { UIA_PatternIds.UIA_TextChildPatternId, "TextChild" },
            { UIA_PatternIds.UIA_TogglePatternId, "Toggle" },
            { UIA_PatternIds.UIA_TransformPatternId, "Transform" },
            { UIA_PatternIds.UIA_TransformPattern2Id, "Transform2" },
            { UIA_PatternIds.UIA_ValuePatternId, "Value" },
            { UIA_PatternIds.UIA_VirtualizedItemPatternId, "VirtualizedItem" },
            { UIA_PatternIds.UIA_WindowPatternId, "Window" },
        };
    }
}

using Avalonia;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.App;

public static class LocalizedResources
{
    public static void Apply(Application application)
    {
        var resources = application.Resources;
        resources["DragPreviewControlDockString"] = UiStrings.DockPreviewDock;
        resources["DragPreviewControlFloatString"] = UiStrings.DockPreviewFloat;
        resources["DragPreviewControlNoneString"] = UiStrings.FilterNone;
        resources["DocumentTabStripItemFloatString"] = UiStrings.DockFloat;
        resources["DocumentTabStripItemFloatAllString"] = UiStrings.DockFloatAll;
        resources["DocumentTabStripItemCloseString"] = UiStrings.DockClose;
        resources["DocumentTabStripItemCloseOtherTabsString"] = UiStrings.DockCloseOtherTabs;
        resources["DocumentTabStripItemCloseAllTabsString"] = UiStrings.DockCloseAllTabs;
        resources["DocumentTabStripItemCloseTabsLeftString"] = UiStrings.DockCloseTabsLeft;
        resources["DocumentTabStripItemCloseTabsRightString"] = UiStrings.DockCloseTabsRight;
        resources["DocumentTabStripItemCloseTabsAboveString"] = UiStrings.DockCloseTabsAbove;
        resources["DocumentTabStripItemCloseTabsBelowString"] = UiStrings.DockCloseTabsBelow;
        resources["DocumentTabStripItemNewHorizontalDockString"] = UiStrings.DockNewHorizontal;
        resources["DocumentTabStripItemNewVerticalDockString"] = UiStrings.DockNewVertical;
        resources["DocumentTabRevealInFileExplorerString"] = UiStrings.DockRevealInFileExplorer;
        resources["DocumentTabRevealInSolutionExplorerString"] = UiStrings.DockRevealInSolutionExplorer;
        resources["DocumentTabCopyPathString"] = UiStrings.DockCopyPath;
        resources["DocumentTabCopyFileNameString"] = UiStrings.DockCopyFileName;
        resources["DocumentTabStripItemTabLayoutString"] = UiStrings.DockTabLayout;
        resources["DocumentTabStripItemTabLayoutLeftString"] = UiStrings.DockTabsLeft;
        resources["DocumentTabStripItemTabLayoutTopString"] = UiStrings.DockTabsTop;
        resources["DocumentTabStripItemTabLayoutRightString"] = UiStrings.DockTabsRight;
        resources["DocumentTabStripItemLayoutModeString"] = UiStrings.DockLayoutMode;
        resources["DocumentTabStripItemLayoutModeTabbedString"] = UiStrings.DockTabbed;
        resources["ToolPinItemControlFloatString"] = UiStrings.DockFloat;
        resources["ToolPinItemControlShowString"] = UiStrings.DockShow;
        resources["ToolPinItemControlDockAsDocumentString"] = UiStrings.DockAsDocument;
        resources["ToolPinItemControlCloseString"] = UiStrings.DockClose;
        resources["ToolChromeControlFloatString"] = UiStrings.DockFloat;
        resources["ToolChromeControlDockString"] = UiStrings.DockDock;
        resources["ToolChromeControlAutoHideString"] = UiStrings.DockAutoHide;
        resources["ToolChromeControlDockAsDocumentString"] = UiStrings.DockAsDocument;
        resources["ToolChromeControlCloseString"] = UiStrings.DockClose;
        resources["ToolTabStripItemFloatString"] = UiStrings.DockFloat;
        resources["ToolTabStripItemFloatAllString"] = UiStrings.DockFloatAll;
        resources["ToolTabStripItemDockString"] = UiStrings.DockDock;
        resources["ToolTabStripItemAutoHideString"] = UiStrings.DockAutoHide;
        resources["ToolTabStripItemDockAsDocumentString"] = UiStrings.DockAsDocument;
        resources["ToolTabStripItemCloseString"] = UiStrings.DockClose;
    }
}

using System.Windows.Automation;
using Microsoft.UI.Xaml;

namespace Hotshot.ProductHarness;

internal static class NativeUiAutomation
{
    public static Task SetTextAsync(Window window, string automationId, string value)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        // TextBox's ValuePattern is native-only, not the WinRT peer's GetPattern surface.
        // UIA clients must run off the UI thread; search only this fixture-owned HWND.
        return Task.Run(() =>
        {
            var element = Find(Root(handle), automationId);
            if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern text)
                throw new InvalidOperationException("Native UI Automation ValuePattern is missing: " + automationId);
            if (text.Current.IsReadOnly)
                throw new InvalidOperationException("Native UI Automation input is read-only: " + automationId);
            text.SetValue(value);
            if (text.Current.Value != value)
                throw new InvalidDataException("Native UI Automation did not update the real input: " + automationId);
        });
    }

    public static Task SelectAsync(Window window, string automationId, string itemName)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return Task.Run(() =>
        {
            var root = Root(handle);
            var combo = Find(root, automationId);
            if (!combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) || pattern is not ExpandCollapsePattern expansion)
                throw new InvalidOperationException("Native UI Automation ExpandCollapsePattern is missing: " + automationId);
            expansion.Expand();
            try
            {
                var condition = new AndCondition(new PropertyCondition(AutomationElement.NameProperty, itemName),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                AutomationElement? item;
                while ((item = root.FindFirst(TreeScope.Descendants, condition)) is null)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                        throw new TimeoutException("Native UI Automation dropdown item did not appear within its own window: " + itemName);
                    Thread.Sleep(50);
                }
                RequireOwnProcess(item);
                if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) || selection is not SelectionItemPattern selectable)
                    throw new InvalidOperationException("Native UI Automation SelectionItemPattern is missing: " + itemName);
                selectable.Select();
                if (!selectable.Current.IsSelected)
                    throw new InvalidDataException("Native UI Automation did not select the actual dropdown item: " + itemName);
            }
            finally
            {
                if (expansion.Current.ExpandCollapseState != ExpandCollapseState.Collapsed) expansion.Collapse();
            }
        });
    }

    public static Task SetRangeAsync(Window window, string automationId, double value)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        return Task.Run(() =>
        {
            var element = Find(Root(handle), automationId);
            if (!element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pattern) || pattern is not RangeValuePattern range)
                throw new InvalidOperationException("Native UI Automation RangeValuePattern is missing: " + automationId);
            range.SetValue(value);
            if (range.Current.Value != value)
                throw new InvalidDataException("Native UI Automation did not update the actual range control: " + automationId);
        });
    }

    private static AutomationElement Root(nint handle)
    {
        var root = AutomationElement.FromHandle(handle)
            ?? throw new InvalidOperationException("The fixture-owned window has no native UI Automation root.");
        RequireOwnProcess(root);
        return root;
    }

    private static AutomationElement Find(AutomationElement root, string automationId)
    {
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        AutomationElement? element;
        while ((element = root.FindFirst(TreeScope.Descendants, condition)) is null)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                Console.Error.WriteLine($"Native UIA own-window root: {root.Current.Name}, descendants={descendants.Count}, missing={automationId}.");
                for (var index = 0; index < Math.Min(40, descendants.Count); index++)
                {
                    var current = descendants[index].Current;
                    if (current.ProcessId == Environment.ProcessId)
                        Console.Error.WriteLine($"Native UIA own control: type={current.ControlType.ProgrammaticName}, id={current.AutomationId}, offscreen={current.IsOffscreen}.");
                }
                throw new InvalidOperationException("Native UI Automation control is missing: " + automationId);
            }
            Thread.Sleep(50);
        }
        RequireOwnProcess(element);
        return element;
    }

    private static void RequireOwnProcess(AutomationElement element)
    {
        if (element.Current.ProcessId != Environment.ProcessId)
            throw new InvalidOperationException("Native UI Automation must never target another process.");
    }
}

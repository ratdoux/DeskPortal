using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using UiPoint = System.Windows.Point;

namespace DeskLink;

// WinUI's InputSiteWindowClass handles system pointer input. Its controls also
// expose actions through UI Automation, scoped here to the shared source HWND.
sealed class WinUiInput(nint hwnd)
{
    public Point? Press;
    public bool DoubleClick;
    public bool ExplorerTabPress;
    volatile bool stopped;
    int verticalRemainder, horizontalRemainder;
    public void Stop() => stopped = true;

    public static bool IsInputSite(nint target) => Win.Class(target) is "InputSiteWindowClass"
        or "WinUIDesktopWin32WindowClass" or "Microsoft.UI.Content.DesktopChildSiteBridge"
        or "Windows.UI.Input.InputSite.WindowClass" or "Windows.UI.Composition.DesktopWindowContentBridge" or "XamlWindow";

    static bool Pattern<T>(AutomationElement e, AutomationPattern pattern, out T value) where T : class
    {
        if (e.TryGetCurrentPattern(pattern, out var result) && result is T found) { value = found; return true; }
        value = null!; return false;
    }

    List<AutomationElement> At(Point point, bool explorerTabs = false)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (explorerTabs)
        {
            var tabs = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "TabView")));
            if (tabs != null) root = tabs;
        }
        var pos = new UiPoint(point.X, point.Y);
        var cache = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.ControlViewCondition };
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.IsEnabledProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsKeyboardFocusableProperty);
        var branch = new List<AutomationElement>();
        var path = new List<AutomationElement>();
        double smallest = double.MaxValue;
        void Visit(AutomationElement element)
        {
            branch.Add(element);
            var rect = element.Cached.BoundingRectangle;
            double area = rect.Width * rect.Height;
            if (!element.Cached.IsOffscreen && rect.Contains(pos) && area <= smallest)
            {
                smallest = area;
                path.Clear(); path.AddRange(branch);
            }
            if (branch.Count < 128 && element.CachedChildren is { } children)
                foreach (AutomationElement child in children) Visit(child);
            branch.RemoveAt(branch.Count - 1);
        }
        Visit(root.GetUpdatedCache(cache));
        path.Reverse();
        return path;
    }

    static void Focus(AutomationElement e)
    {
        if (!e.Cached.IsKeyboardFocusable) return;
        try { e.SetFocus(); }
        catch (InvalidOperationException) { /* Some actionable composite controls delegate focus to a child. */ }
    }

    public bool Click(Point start, Point end, bool twice)
    {
        foreach (var e in At(start, ExplorerTabPress))
        {
            if (stopped) return true;
            var c = e.Cached;
            bool dragged = Math.Abs(start.X - end.X) >= SystemInformation.DragSize.Width ||
                Math.Abs(start.Y - end.Y) >= SystemInformation.DragSize.Height;
            if (!c.IsEnabled) return true;
            if (c.ControlType == ControlType.Edit)
            {
                Focus(e);
                if (Pattern<TextPattern>(e, TextPattern.Pattern, out var text) && text.SupportedTextSelection != SupportedTextSelection.None)
                {
                    var a = text.RangeFromPoint(new(start.X, start.Y));
                    var b = text.RangeFromPoint(new(end.X, end.Y));
                    a.MoveEndpointByRange(TextPatternRangeEndpoint.End, a, TextPatternRangeEndpoint.Start);
                    if (dragged)
                    {
                        var side = a.CompareEndpoints(TextPatternRangeEndpoint.Start, b, TextPatternRangeEndpoint.Start) <= 0
                            ? TextPatternRangeEndpoint.End : TextPatternRangeEndpoint.Start;
                        a.MoveEndpointByRange(side, b, TextPatternRangeEndpoint.Start);
                    }
                    else if (twice) a.ExpandToEnclosingUnit(TextUnit.Word);
                    a.Select();
                }
                return true;
            }
            if (c.ControlType == ControlType.Slider && Pattern<RangeValuePattern>(e, RangeValuePattern.Pattern, out var range))
            {
                if (range.Current.IsReadOnly) return true;
                var r = c.BoundingRectangle;
                double fraction = r.Width >= r.Height ? (end.X - r.Left) / r.Width : (r.Bottom - end.Y) / r.Height;
                range.SetValue(range.Current.Minimum + Math.Clamp(fraction, 0, 1) * (range.Current.Maximum - range.Current.Minimum));
                return true;
            }
            if (!c.BoundingRectangle.Contains(new UiPoint(end.X, end.Y))) continue;
            if (Pattern<TogglePattern>(e, TogglePattern.Pattern, out var toggle)) { Focus(e); toggle.Toggle(); return true; }
            if (Pattern<InvokePattern>(e, InvokePattern.Pattern, out var invoke)) { Focus(e); invoke.Invoke(); return true; }
            if (Pattern<ExpandCollapsePattern>(e, ExpandCollapsePattern.Pattern, out var expand) &&
                expand.Current.ExpandCollapseState != ExpandCollapseState.LeafNode)
            {
                Focus(e);
                if (expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded) expand.Collapse(); else expand.Expand();
                return true;
            }
            if (Pattern<SelectionItemPattern>(e, SelectionItemPattern.Pattern, out var item)) { Focus(e); item.Select(); return true; }
        }
        return false;
    }

    public bool Scroll(Point point, bool horizontal, int delta)
    {
        foreach (var e in At(point))
        {
            if (stopped) return true;
            if (!Pattern<ScrollPattern>(e, ScrollPattern.Pattern, out var scroll) ||
                !(horizontal ? scroll.Current.HorizontallyScrollable : scroll.Current.VerticallyScrollable)) continue;
            ref int remainder = ref (horizontal ? ref horizontalRemainder : ref verticalRemainder);
            remainder += delta;
            int steps = remainder / 120;
            remainder %= 120;
            int lines = SystemInformation.MouseWheelScrollLines;
            bool increase = horizontal ? steps > 0 : steps < 0;
            var amount = lines < 0 ? (increase ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement)
                : (increase ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement);
            int count = Math.Min(32, Math.Abs(steps) * (lines < 0 ? 1 : lines));
            for (int i = 0; i < count && !stopped; i++)
                scroll.Scroll(horizontal ? amount : ScrollAmount.NoAmount, horizontal ? ScrollAmount.NoAmount : amount);
            return true;
        }
        return false;
    }

    public static async Task<bool> TryAction(Func<bool> action)
    {
        // Cross-process providers can block. The peer awaits this task for input
        // ordering, while the WinForms thread keeps painting and handling hooks.
        try { return await Task.Run(action); }
        // A provider in a busy or hung app reports a timeout. The caller then posts the plain message.
        catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException
            or ElementNotAvailableException or ElementNotEnabledException or ArgumentException or TimeoutException) { return false; }
    }
}

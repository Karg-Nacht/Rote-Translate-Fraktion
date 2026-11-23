using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace Rtf;

internal sealed record Selection(nint Window, string Text, Rect? Bounds, Native.Point Fallback);

internal sealed class SelectionMonitor : IDisposable
{
    private const int SettleMilliseconds = 380;
    private const int TextLimit = 20_000;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private readonly Action<Selection?, int> publish;
    private AutomationElement? subscribedRoot;
    private AutomationEventHandler? selectionHandler;
    private nint foreground;
    private AutomationElement? senderHint;
    private Native.Point? pointerHint;
    private Native.Point? lastPointer;
    private bool focusChanged;
    private bool stopping;
    private int revision;

    public SelectionMonitor(Action<Selection?, int> publish)
    {
        this.publish = publish;
        worker = new Thread(Run) { IsBackground = true, Name = "RTF UI Automation" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public bool IsCurrent(int version) => version == Volatile.Read(ref revision);

    public void ForegroundChanged() => Request(reset: true);
    public void InteractionEnded(Native.Point? point) => Request(point: point);

    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs args) =>
        Request(reset: true);

    private void Request(bool reset = false, AutomationElement? sender = null, Native.Point? point = null)
    {
        lock (gate)
        {
            if (stopping)
                return;

            if (reset)
            {
                focusChanged = true;
                senderHint = null;
                pointerHint = null;
            }

            senderHint = sender ?? senderHint;
            pointerHint = point ?? pointerHint;
            revision++;
            wake.Set();
        }
    }

    private void Run()
    {
        try
        {
            try { Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged); }
            catch (Exception error) when (ProviderFailure(error)) { }
            Request();

            while (wake.WaitOne())
            {
                while (true)
                {
                    lock (gate)
                    {
                        if (stopping)
                            return;
                    }

                    RefreshSubscription();
                    if (!wake.WaitOne(SettleMilliseconds))
                        break;
                }

                AutomationElement? sender;
                Native.Point? pointer;
                bool reset;
                int version;
                lock (gate)
                {
                    if (stopping)
                        return;

                    // A notification may have arrived after the wait timed out.
                    if (wake.WaitOne(0))
                    {
                        wake.Set();
                        continue;
                    }

                    (sender, pointer, reset, version) = (senderHint, pointerHint, focusChanged, revision);
                    senderHint = null;
                    pointerHint = null;
                    focusChanged = false;
                }

                if (reset)
                    lastPointer = null;
                if (pointer is { } point && IsOverForeground(point))
                    lastPointer = point;

                // A mouse selection is provisional until the button is released.
                if (Native.GetAsyncKeyState(1) < 0 || Native.GetAsyncKeyState(2) < 0)
                    continue;

                var selection = ReadSelection(sender, pointer);
                if (IsCurrent(version) && foreground == Native.GetForegroundWindow())
                    publish(selection, version);
            }
        }
        finally
        {
            RemoveSubscription();
            try { Automation.RemoveAutomationFocusChangedEventHandler(OnFocusChanged); }
            catch (Exception error) when (ProviderFailure(error)) { }
            wake.Dispose();
        }
    }

    private void RefreshSubscription()
    {
        nint hwnd = Native.GetForegroundWindow();
        if (hwnd == foreground && subscribedRoot is not null)
            return;

        if (hwnd != foreground)
        {
            RemoveSubscription();
            foreground = hwnd;
            lastPointer = null;
        }

        if (hwnd == 0 || Native.IsOwnWindow(hwnd))
            return;

        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            AutomationEventHandler handler = (sender, _) =>
            {
                if (Native.GetForegroundWindow() == hwnd && sender is AutomationElement element)
                    Request(sender: element);
            };
            Automation.AddAutomationEventHandler(TextPattern.TextSelectionChangedEvent,
                root, TreeScope.Subtree, handler);
            subscribedRoot = root;
            selectionHandler = handler;
        }
        catch (Exception error) when (ProviderFailure(error)) { }
    }

    private void RemoveSubscription()
    {
        if (subscribedRoot is null || selectionHandler is null)
            return;

        try
        {
            Automation.RemoveAutomationEventHandler(TextPattern.TextSelectionChangedEvent,
                subscribedRoot, selectionHandler);
        }
        catch (Exception error) when (ProviderFailure(error)) { }
        finally
        {
            subscribedRoot = null;
            selectionHandler = null;
        }
    }

    private Selection? ReadSelection(AutomationElement? sender, Native.Point? pointer)
    {
        if (foreground == 0 || Native.IsOwnWindow(foreground))
            return null;

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused?.Current.IsPassword == true)
                return null;

            // On a mouse gesture the hit provider is a better candidate than
            // the keyboard-focused element, particularly for web documents.
            if (pointer is { } click && TryReadAtPoint(click, out var fromClick))
                return fromClick;

            if (sender is not null && TryReadFromAncestors(sender, out var fromEvent))
                return fromEvent;

            if (focused is not null && TryReadFromAncestors(focused, out var fromFocus))
                return fromFocus;

            if (lastPointer is { } last && TryReadAtPoint(last, out var fromDocument))
                return fromDocument;
        }
        catch (Exception error) when (ProviderFailure(error)) { }

        return null;
    }

    private bool IsOverForeground(Native.Point point) =>
        Native.GetAncestor(Native.WindowFromPoint(point), 2) == foreground;

    private bool TryReadAtPoint(Native.Point point, out Selection? selection)
    {
        selection = null;
        try
        {
            return IsOverForeground(point) &&
                TryReadFromAncestors(AutomationElement.FromPoint(new Point(point.X, point.Y)), out selection);
        }
        catch (Exception error) when (ProviderFailure(error)) { return false; }
    }

    private bool TryReadFromAncestors(AutomationElement? element, out Selection? selection)
    {
        selection = null;
        try
        {
            for (int depth = 0; element is not null && depth < 24; depth++)
            {
                if (element.Current.IsPassword)
                    return true;

                nint handle = element.Current.NativeWindowHandle;
                if (handle != 0 && handle != foreground && Native.GetAncestor(handle, 2) != foreground)
                    return false;

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object provider) &&
                    provider is TextPattern pattern && pattern.SupportedTextSelection != SupportedTextSelection.None)
                {
                    selection = ReadRanges(pattern);
                    return true;
                }

                if (handle == foreground)
                    break;

                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        catch (Exception error) when (ProviderFailure(error)) { }

        return false;
    }

    private Selection? ReadRanges(TextPattern pattern)
    {
        var parts = new List<string>();
        Rect? anchor = null;
        int remaining = TextLimit;

        foreach (var range in pattern.GetSelection())
        {
            string text = range.GetText(remaining);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            parts.Add(text);
            remaining -= text.Length + 1;

            if (anchor is null)
            {
                try
                {
                    foreach (Rect rect in range.GetBoundingRectangles())
                    {
                        if (rect.Width > 0 && rect.Height > 0 && double.IsFinite(rect.Right) &&
                            double.IsFinite(rect.Bottom) && double.IsFinite(rect.X) && double.IsFinite(rect.Y))
                        {
                            anchor = rect;
                            break;
                        }
                    }
                }
                catch (Exception error) when (ProviderFailure(error)) { }
            }

            if (remaining <= 0)
                break;
        }

        if (parts.Count == 0)
            return null;

        Native.GetCursorPos(out var cursor);
        if (!IsOverForeground(cursor) && Native.GetWindowRect(foreground, out var windowRect))
            cursor = new Native.Point(windowRect.Left + (windowRect.Right - windowRect.Left) / 2, windowRect.Top + 48);

        return new Selection(foreground, string.Join("\n", parts), anchor, cursor);
    }

    private static bool ProviderFailure(Exception error) =>
        error is COMException or InvalidOperationException or ArgumentException or Win32Exception
            or TimeoutException or UnauthorizedAccessException;

    public void Dispose()
    {
        lock (gate)
        {
            if (stopping)
                return;
            stopping = true;
            wake.Set();
        }

        // A foreign provider can be slow. Keep application shutdown bounded.
        worker.Join(500);
    }
}

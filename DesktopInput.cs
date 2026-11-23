using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Rtf;

internal sealed class DesktopInput : IDisposable
{
    private readonly Native.WinEventCallback foregroundCallback;
    private readonly Native.HookCallback mouseCallback;
    private readonly Native.HookCallback keyboardCallback;
    private readonly DispatcherTimer middleHold = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private Native.Point? middleStart;
    private Action<Native.Point, int, bool>? pointerTracker;
    private DispatcherOperation? pointerDispatch;
    private Native.Point lastTrackedPoint;
    private int lastTrackedTime;
    private nint foregroundHook, mouseHook, keyboardHook;

    public DesktopInput(Action foregroundChanged, Action<Native.Point?> interactionEnded, Action<Native.Point> middleHeld,
        Action<Native.Point> overlayPressed)
    {
        middleHold.Tick += (_, _) =>
        {
            middleHold.Stop();
            bool pending = middleStart is not null;
            middleStart = null;
            if (pending && (Native.GetAsyncKeyState(0x04) & 0x8000) != 0 &&
                Native.GetCursorPos(out var cursor) && !Native.IsOwnWindow(Native.WindowFromPoint(cursor)))
                middleHeld(cursor);
        };
        foregroundCallback = (_, _, _, _, _, _, _) => foregroundChanged();
        mouseCallback = (code, message, data) =>
        {
            if (code >= 0 && pointerTracker is { } tracker && message is 0x0200 or 0x0202)
            {
                TrackPointerEvent(tracker, Marshal.PtrToStructure<Native.Point>(data), release: message == 0x0202);
                return Native.CallNextHookEx(0, code, message, data);
            }
            if (code >= 0 && (message is 0x0201 or 0x0202 or 0x0207 or 0x0208 ||
                message == 0x0200 && middleStart is not null))
            {
                var point = Marshal.PtrToStructure<Native.Point>(data);
                if (message == 0x0201 && Native.IsOwnWindow(Native.WindowFromPoint(point)))
                    overlayPressed(point);
                else if (message == 0x0202 && !Native.IsOwnWindow(Native.WindowFromPoint(point)))
                    interactionEnded(point);
                else if (message == 0x0207)
                {
                    CancelMiddleHold();
                    if (!Native.IsOwnWindow(Native.WindowFromPoint(point)))
                    {
                        middleStart = point;
                        middleHold.Start();
                    }
                }
                else if (message == 0x0208 ||
                         middleStart is { } start && (Math.Abs(point.X - start.X) > 12 || Math.Abs(point.Y - start.Y) > 12))
                    CancelMiddleHold();
            }
            // Deliver a matching mouse-up to applications that received the original mouse-down.
            return Native.CallNextHookEx(0, code, message, data);
        };
        keyboardCallback = (code, message, data) =>
        {
            if (code >= 0 && message == 0x0101) // WM_KEYUP
            {
                int key = Marshal.ReadInt32(data);
                // Do not retain keys or assemble typed text.
                if (key is not (0x10 or 0x11 or 0x12 or >= 0xA0 and <= 0xA5))
                    interactionEnded(null);
            }
            return Native.CallNextHookEx(0, code, message, data);
        };

        try
        {
            foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
            if (foregroundHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            nint module = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
            if (mouseHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            keyboardHook = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
            if (keyboardHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        pointerTracker = null;
        pointerDispatch?.Abort();
        CancelMiddleHold();
        if (keyboardHook != 0)
            Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != 0)
            Native.UnhookWindowsHookEx(mouseHook);
        if (foregroundHook != 0)
            Native.UnhookWinEvent(foregroundHook);
        keyboardHook = mouseHook = foregroundHook = 0;
    }

    // A no-activate overlay cannot hold Win32 capture across other windows.
    internal void TrackLeftPointer(Action<Native.Point, int, bool> update) => pointerTracker = update;

    private void TrackPointerEvent(Action<Native.Point, int, bool> tracker, Native.Point point, bool release)
    {
        lastTrackedPoint = point;
        lastTrackedTime = Environment.TickCount;
        if (release)
        {
            pointerTracker = null;
            pointerDispatch?.Abort();
            pointerDispatch = null;
            int time = lastTrackedTime;
            dispatcher.BeginInvoke(DispatcherPriority.Input, () => tracker(point, time, true));
        }
        else if (pointerDispatch is null)
        {
            pointerDispatch = dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                pointerDispatch = null;
                tracker(lastTrackedPoint, lastTrackedTime, false);
            });
        }
    }

    private void CancelMiddleHold()
    {
        middleHold.Stop();
        middleStart = null;
    }
}

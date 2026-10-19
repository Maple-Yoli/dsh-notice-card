// The notice card itself, as a standalone WinForms executable.
//
// This is the same card the PowerShell version draws - Windows toast styling,
// bottom-right corner, a deck of stacked cards, no focus stealing - but compiled
// ahead of time instead of interpreted on every appearance. That removes the
// PowerShell start, the Add-Type compile and the runspace from the path between
// "DSH is waiting" and "the card is on screen": 57ms instead of 650ms, and no
// shared helper process, so one card can never take another down with it.
//
// Compiled by the plugin with the .NET Framework csc.exe that ships with Windows
// (see lib/index.mjs). One process per card; the process exits when the card
// closes. Nothing is resident and no state is shared between cards except the
// deck files in %TEMP%\dsh-notice-cards, which only carry stacking order.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Both System.Threading and System.Windows.Forms define Timer, and the card uses
// the WinForms one everywhere except inside Watchdog, where the threading timer
// is spelled out in full.
using Timer = System.Windows.Forms.Timer;

static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(int value);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    // Per-monitor dpi. The card is drawn on whichever monitor it lands on, and a
    // 125% primary next to a 100% secondary would otherwise scale the card on the
    // second monitor by the first one's ratio. shcore only exists on Windows 8.1
    // and later, so the caller falls back to the screen dc.
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr hmon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    public static float DpiOf(Rectangle bounds)
    {
        try
        {
            POINT pt;
            pt.X = bounds.Left + bounds.Width / 2;
            pt.Y = bounds.Top + bounds.Height / 2;
            IntPtr mon = MonitorFromPoint(pt, 2);   // MONITOR_DEFAULTTONEAREST
            if (mon != IntPtr.Zero)
            {
                uint dx, dy;
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96f;
            }
        }
        catch { }
        using (Graphics probe = Graphics.FromHwnd(IntPtr.Zero)) return probe.DpiX / 96f;
    }

    // Windows 11 keeps the app theme in the registry, and there is no supported
    // API for a plain desktop app to ask. Missing key or an unexpected value
    // means light, which is also the Windows default.
    public static bool AppsUseDarkTheme()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key == null) return false;
                object value = key.GetValue("AppsUseLightTheme");
                if (value is int) return (int)value == 0;
            }
        }
        catch { }
        return false;
    }

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out int data, uint winIni);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    const uint SPI_GETWHEELSCROLLLINES = 0x0068;
    const int SM_DIGITIZER = 94;
    const int NID_INTEGRATED_TOUCH = 0x80;

    // "Show animations in Windows". When it is off the user has told the whole
    // desktop to stop moving, and a card that still slides and fades is exactly
    // what that setting exists to prevent.
    public static bool AnimationsEnabled()
    {
        try
        {
            int on;
            if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out on, 0)) return on != 0;
        }
        catch { }
        try { return SystemInformation.UIEffectsEnabled; } catch { return true; }
    }

    // How many lines one wheel notch should move, as configured in the mouse
    // settings. 0 means "a screen at a time" and the WHEEL_PAGESCROLL sentinel is
    // -1 (use the platform default), both of which fall back to 3 here.
    public static int WheelLines()
    {
        try
        {
            int lines;
            if (SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, out lines, 0) && lines > 0) return Math.Min(10, lines);
        }
        catch { }
        return 3;
    }

    // Is the screen driven by a finger? A touch-first device has no hover at all,
    // so a hover highlight is dead weight and the buttons need to be large enough
    // to hit with a fingertip.
    public static bool TouchPresent()
    {
        try
        {
            int bits = GetSystemMetrics(SM_DIGITIZER);
            return (bits & NID_INTEGRATED_TOUCH) != 0;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    // Battery saver, or a low battery: the user is asking the machine to draw
    // less, and an animation is the first thing that can go without costing any
    // information. Fires per card, so a machine that drops into saver mode while a
    // deck is up changes behaviour on the next card.
    public static bool DrawingLess()
    {
        try
        {
            SYSTEM_POWER_STATUS s;
            if (!GetSystemPowerStatus(out s)) return false;
            if (s.SystemStatusFlag == 1) return true;
            return s.ACLineStatus == 0 && s.BatteryLifePercent <= 20;
        }
        catch { return false; }
    }

    // High contrast is a statement about colour, not a preference: the palette
    // below is replaced wholesale when it is on.
    public static bool HighContrast()
    {
        try { return SystemInformation.HighContrast; } catch { return false; }
    }

    // "Make text bigger" (Windows 10 1809 and later, settings > accessibility).
    // A percentage in the registry, 100 by default, missing on older builds. The
    // card scales as a whole rather than reflowing, so text and the space around
    // it stay in proportion at every setting.
    public static float TextScale()
    {
        try
        {
            using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Accessibility"))
            {
                if (key != null)
                {
                    object value = key.GetValue("TextScaleFactor");
                    if (value is int)
                    {
                        float factor = (int)value / 100f;
                        if (factor > 1f && factor <= 2.25f) return factor;
                    }
                }
            }
        }
        catch { }
        return 1f;
    }

    public const uint LWA_ALPHA = 0x2;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int GWL_EXSTYLE = -20;
    public const uint WM_CLOSE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public static void PostFrame(IntPtr h) { PostMessage(h, Card.WM_FRAME, IntPtr.Zero, IntPtr.Zero); }

    // A card is a normal top-level window, so putting one on screen over an
    // exclusive-fullscreen game forces the display mode to change and the screen
    // blinks black. No single API answers "is a game exclusive-fullscreen": the
    // shell's own state plus the shape of the foreground window do.
    //
    // QUNS_BUSY (2) is what a borderless full-screen video or an F11 browser
    // reports and the compositor is still running, so a card over it costs
    // nothing. Only D3D exclusive (3) and presentation mode (4) really take the
    // display away.
    static bool CoversScreen()
    {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero || IsIconic(h) || !IsWindowVisible(h)) return false;
        RECT r;
        if (!GetWindowRect(h, out r)) return false;
        if (r.R - r.L < Screen.PrimaryScreen.Bounds.Width || r.B - r.T < Screen.PrimaryScreen.Bounds.Height) return false;
        // The shell draws the desktop itself; it owns the screen and is not a game.
        StringBuilder cls = new StringBuilder(64);
        GetClassNameW(h, cls, 64);
        string c = cls.ToString();
        return c != "Progman" && c != "WorkerW";
    }

    public static bool ExclusiveFullScreen()
    {
        int state;
        if (SHQueryUserNotificationState(out state) != 0) return false;
        if (state != 3 && state != 4) return false;
        return CoversScreen();
    }

    // Quiet hours: the user told Windows not to interrupt. A turn-end card is an
    // interruption and is dropped; an approval or a question is the conversation
    // blocking on a human, so it still shows.
    public static bool QuietTime()
    {
        try
        {
            int state;
            if (SHQueryUserNotificationState(out state) != 0) return false;
            return state == 6;
        }
        catch { return false; }
    }

    // Is something owning the whole screen right now? The card asks this to decide
    // whether it may eat the mouse. A notice drawn over a full-screen app is the
    // one case where the rectangle it covers is a rectangle the app below can no
    // longer receive the cursor in, and that reads as a dead patch on the screen.
    public static bool FullScreenForeground()
    {
        return CoversScreen();
    }

    // Is the reader looking at this process right now? A card that waits for an
    // answer asks that question, because a request can be settled with nobody at
    // the desk. The answer alone is no evidence of anything; the window the
    // reader is looking at is.
    public static bool ForegroundIs(string name)
    {
        if (name.Length == 0) return false;
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        int pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid == 0) return false;
        try
        {
            using (Process p = Process.GetProcessById(pid))
                return string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool ProcessAlive(int pid)
    {
        if (pid <= 0) return true;
        try { using (Process p = Process.GetProcessById(pid)) return !p.HasExited; }
        catch { return false; }
    }

    public static byte CurrentAlpha(IntPtr h)
    {
        uint k, f;
        byte a;
        if (!GetLayeredWindowAttributes(h, out k, out a, out f)) return 255;
        return (f & LWA_ALPHA) != 0 ? a : (byte)255;
    }

    public static void ForceClose(IntPtr h)
    {
        if (!IsWindow(h)) return;
        PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(500);
        if (IsWindow(h)) { try { DestroyWindow(h); } catch { } }
    }
}

// A hand-painted panel erases its background and only then draws, so every
// repaint shows a flash between the two. Double buffering collapses erase and
// paint into a single blit.
sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}

// The watchdog primitives. Opacity starts at 0 and is lifted only by the frame
// pump, and the hard lifetime bound is a timer on the card's own message loop.
// Both live there, so if that loop stalls the window stays alive, topmost and
// visible while showing nothing at all - a card nobody can see and that never
// goes away. These need neither the message loop nor the UI thread, so a pool
// thread can always rescue the card.
//
// Both deliberately avoid touching the form object: the pool thread has no idea
// whether the UI thread is inside a message loop, and poking a window that is
// pumping messages from another thread is what turns a stuck card into a dead
// one. These are pure Win32 calls on the handle instead.
static class Watchdog
{
    static IntPtr handle = IntPtr.Zero;
    static int slotX, slotY;
    static System.Threading.Timer settle, life;
    static bool settleArmed;
    static readonly object gate = new object();

    // lifeMs <= 0 means no hard deadline at all. The caller passes one only for a
    // turn-end card, which lives for `seconds`; a card waiting on a human gets
    // none, because expiring unseen is exactly the failure it exists to prevent.
    public static void Arm(IntPtr h, int x, int y, int settleMs, int lifeMs)
    {
        Disarm();
        lock (gate)
        {
            handle = h; slotX = x; slotY = y; settleArmed = true;
            settle = new System.Threading.Timer(Settle, null, settleMs, Timeout.Infinite);
            if (lifeMs > 0) life = new System.Threading.Timer(Expire, null, lifeMs, Timeout.Infinite);
        }
    }

    public static void DisarmSettle()
    {
        lock (gate)
        {
            settleArmed = false;
            if (settle != null) { try { settle.Dispose(); } catch { } settle = null; }
        }
    }

    public static void Disarm()
    {
        DisarmSettle();
        lock (gate)
        {
            handle = IntPtr.Zero;
            if (life != null) { try { life.Dispose(); } catch { } life = null; }
        }
    }

    // Re-arms only the deadline, leaving the handle alone. A card that waits for
    // an answer has no deadline while it waits - it must not vanish before it was
    // read - so the only bound on it is scratched in when it decides to leave: if
    // the exit fade then wedges, this still takes the window down.
    public static void ArmDeadline(int ms)
    {
        lock (gate)
        {
            if (life != null) { try { life.Dispose(); } catch { } life = null; }
            if (ms > 0 && handle != IntPtr.Zero) life = new System.Threading.Timer(Expire, null, ms, Timeout.Infinite);
        }
    }

    // Still transparent well after the entrance should have finished - the pump
    // stopped, the loop is stuck, a message was lost - so paint it opaque and put
    // it back in its slot. A card that animated on its own is never touched,
    // because the alpha check gates this.
    static void Settle(object state)
    {
        IntPtr h; int x, y;
        lock (gate)
        {
            // DisarmSettle can run after this callback is already queued, and by
            // then the card may be fading out under its own power: forcing it
            // opaque mid-fade would put a leaving card back on screen.
            if (!settleArmed) return;
            h = handle; x = slotX; y = slotY;
        }
        // The Win32 calls stay outside the lock. SetWindowPos can block when the
        // window belongs to the thread that is stuck, and the deadline timer has
        // to stay able to fire while that happens.
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
        if (Native.CurrentAlpha(h) >= 250) return;
        try { Native.SetLayeredWindowAttributes(h, 0, 255, Native.LWA_ALPHA); } catch { }
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
    }

    // The hard lifetime bound, independent of the WinForms life timer. Fires only
    // if the card is somehow still up long after it should have gone, at which
    // point there is nothing left to animate and closing it abruptly is right.
    static void Expire(object state)
    {
        IntPtr h;
        lock (gate) { h = handle; handle = IntPtr.Zero; }
        if (h == IntPtr.Zero) return;
        Native.ForceClose(h);
    }
}

sealed class Options
{
    public string Title = "DeepSeek Harness";
    public string Body = "任务已结束，等你下一步。";
    public int Seconds = 10;
    public string DismissFile = "";
    public string ProcessName = "DeepSeek Harness";
    public string AppPath = "";
    public int OwnerPid = 0;
    public bool Debug = false;
    // The permission mode the session is in, already written out by the plugin:
    // "权限 工作区内修改". The prefix is part of the string so the wording follows
    // the reader's UI language instead of being baked into the card.
    public string Preset = "";
    // Which session the notice belongs to, already named by the plugin: the
    // session title when the host has one, its short id otherwise.
    public string Session = "";
    // Where the deck lives: the corner Windows puts its own toasts in when the
    // setting says so, otherwise whatever the config names. "auto" reads the
    // taskbar: a bottom taskbar keeps the cards bottom-right like the shell, a
    // top one puts them top-right, a side one moves them to that side.
    public string Corner = "auto";
    // How many cards may be on screen at once. 0 or less means "as many as fit":
    // the deck height is the screen's, so a short laptop shows fewer than a tall
    // monitor instead of running off the top.
    public int MaxStack = 0;

    public static Options Parse(string[] args)
    {
        Options o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string v = i + 1 < args.Length ? args[i + 1] : "";
            switch (a)
            {
                case "--title": o.Title = v; i++; break;
                case "--body": o.Body = v; i++; break;
                case "--seconds": { int n; if (int.TryParse(v, out n)) o.Seconds = n; i++; break; }
                case "--dismiss": o.DismissFile = v; i++; break;
                case "--process": o.ProcessName = v; i++; break;
                case "--app": o.AppPath = v; i++; break;
                case "--owner-pid": { int n; if (int.TryParse(v, out n)) o.OwnerPid = n; i++; break; }
                case "--preset": o.Preset = v; i++; break;
                case "--session": o.Session = v; i++; break;
                case "--corner": o.Corner = v; i++; break;
                case "--max-stack": { int n; if (int.TryParse(v, out n)) o.MaxStack = n; i++; break; }
            }
        }
        if (o.Seconds < 1) o.Seconds = 1;
        return o;
    }
}

sealed class Card : Form
{
    public const int WM_FRAME = 0x8000 + 1;

    // The pump posts a private message from a background thread instead of using
    // a WinForms timer. WM_TIMER is coalesced to the 15.6ms system tick, so an
    // 8ms animation really paints at ~55fps with jitter; at 4ms this covers a
    // 180Hz display and the motion is a continuous glide.
    Action frame;
    Thread pump;
    volatile bool pumping;
    IntPtr pumpTarget;

    readonly Options opt;
    readonly float scale;
    readonly float unit;
    readonly Font captionFont;
    readonly Font bodyFont;
    readonly Font metaFont;

    // The card follows the Windows app theme. There is no API to subscribe to a
    // change, and a notice lives seconds, so the theme is read once when the card
    // is built: a card raised after the switch already has the new colours.
    readonly bool dark;
    readonly bool contrast;
    readonly bool animated;
    readonly bool touch;
    readonly bool rtl;
    readonly int wheelLines;
    readonly Color surface;
    readonly Color titleColor;
    readonly Color bodyColor;
    readonly Color mutedColor;
    readonly Color hairlineColor;
    readonly Color hoverColor;
    readonly Color trackColor;
    readonly Color thumbColor;

    readonly string cardDir;
    readonly string cardFile;
    // Short tag derived from the corner, so the deck files of different corners
    // stay apart in the shared temp directory.
    readonly string cornerKey;
    IntPtr prevHwnd = IntPtr.Zero;

    // The card grows out of the corner it is anchored to, and the deck runs away
    // from that edge: downward from a top corner, upward from a bottom one.
    readonly Rectangle area;
    readonly bool topCorner, leftCorner;
    readonly int targetX, targetY, slotBase, slotOffset;
    readonly int bodyWidth, bodyHeight, barWidth, lineHeight;
    readonly List<string> bodyLines;
    readonly int maxScroll, thumbHeight;
    readonly GraphicsPath shape;

    public int SlotX { get { return targetX; } }
    public int SlotYTarget { get { return targetY; } }

    BufferedPanel bodyBox;
    double scroll, scrollTarget;
    bool dragging, gliding, settled;
    double dragGrab;

    Timer life, reflow, watch;
    bool exiting;
    double exitFromOpacity = 1.0;
    int exitFromY;
    Stopwatch exitSw;
    Stopwatch entranceSw;

    bool answered;
    long answerAt;
    long lastSeen = -1;
    int ownerTick;
    Stopwatch clock;
    const long SeenGraceMs = 3000;

    // A card over a full-screen app must not take the mouse. See ApplyClickThrough.
    bool clickThrough;

    // Anchor for one corner and a scale for it, resolved before the window is
    // built. Every coordinate below is written in 96dpi pixels and multiplied by
    // Px, so the whole card - text, buttons, stacking step - follows the monitor
    // it lands on and the card's size on a small screen as well.
    //
    // The card is placed on the monitor under the mouse rather than the primary
    // one: a card is a response to something the user just did, and the pointer is
    // where they are.
    static Screen PickScreen()
    {
        try
        {
            Screen at = Screen.FromPoint(Cursor.Position);
            if (at != null) return at;
        }
        catch { }
        return Screen.PrimaryScreen;
    }

    // The UI font is picked for the language the card is being read in, because
    // Segoe UI carries no CJK glyphs: on a Chinese or Japanese Windows the shell
    // itself uses a different face, and letting GDI font-link the missing glyphs
    // into Segoe UI is what makes mixed text look like two fonts on one line.
    // Whatever is listed first and installed wins; Segoe UI is the last resort on
    // every Windows since 7.
    static string PickUiFont()
    {
        string name = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        string[] wanted;
        if (name.StartsWith("zh")) wanted = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI Variable Text", "Segoe UI" };
        else if (name.StartsWith("ja")) wanted = new string[] { "Yu Gothic UI", "Meiryo UI", "Segoe UI Variable Text", "Segoe UI" };
        else if (name.StartsWith("ko")) wanted = new string[] { "Malgun Gothic", "Segoe UI Variable Text", "Segoe UI" };
        else wanted = new string[] { "Segoe UI Variable Text", "Segoe UI" };
        foreach (string candidate in wanted)
        {
            try
            {
                foreach (FontFamily family in FontFamily.Families)
                    if (string.Equals(family.Name, candidate, StringComparison.OrdinalIgnoreCase)) return family.Name;
            }
            catch { }
        }
        return "Segoe UI";
    }

    // Right-to-left reading order, from the UI language. Windows itself mirrors
    // its own chrome for these cultures, and so does this card: control buttons
    // swap sides, and the "right" half of a footer line is the one that ends the
    // sentence.
    static bool IsRightToLeft()
    {
        try
        {
            string name = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            return name.StartsWith("ar") || name.StartsWith("he") || name.StartsWith("fa") || name.StartsWith("ur");
        }
        catch { return false; }
    }

    static bool IsTopCorner(string corner)
    {
        return corner == "top-left" || corner == "top-right";
    }

    static bool IsLeftCorner(string corner)
    {
        return corner == "top-left" || corner == "bottom-left";
    }

    // Windows puts its own toasts in the corner the notification area lives in,
    // and that corner follows the taskbar. "auto" copies that rule instead of
    // guessing: a bottom taskbar keeps the cards bottom-right, a top one moves
    // them up, a left one sends them to the bottom-left, a right one leaves them
    // where they are. Any explicit corner wins outright.
    static string ResolveCorner(string corner, Rectangle work, Rectangle bounds)
    {
        switch (corner)
        {
            case "top-left":
            case "top-right":
            case "bottom-left":
            case "bottom-right":
                return corner;
        }
        bool top = work.Top > bounds.Top;
        bool left = work.Left > bounds.Left;
        if (top) return left ? "top-left" : "top-right";
        if (left) return "bottom-left";
        return "bottom-right";
    }

    // The deck may only be as deep as the screen can actually show: every card
    // past the front one costs one slotOffset, and the card that lands off the
    // edge is a card nobody ever sees. So the cap is what fits, and an explicit
    // maxStack is a request that is still clamped to it.
    static int StackLimit(int maxStack, Rectangle area, int height, int slotOffset)
    {
        int fit = 1;
        if (slotOffset > 0) fit = 1 + Math.Max(0, (area.Height - height - 8) / slotOffset);
        fit = Math.Max(1, Math.Min(12, fit));
        if (maxStack > 0) return Math.Max(1, Math.Min(fit, maxStack));
        return fit;
    }

    public Card(Options options)
    {
        opt = options;

        // Per-monitor V2 is set in Main before anything is created, so GDI+ and
        // ClearType render at the real dpi instead of the shell stretching a
        // 96dpi bitmap on a 125% display.
        Screen screen = PickScreen();
        area = screen.WorkingArea;
        scale = Native.DpiOf(area);
        string corner = ResolveCorner(opt.Corner, area, screen.Bounds);
        topCorner = IsTopCorner(corner);
        leftCorner = IsLeftCorner(corner);
        cornerKey = (topCorner ? "t" : "b") + (leftCorner ? "l" : "r");

        dark = Native.AppsUseDarkTheme();
        contrast = Native.HighContrast();
        // Animations off in Windows, or a machine on battery saver, both mean the
        // same thing for this card: skip the motion, keep the pixels.
        animated = Native.AnimationsEnabled() && !Native.DrawingLess();
        touch = Native.TouchPresent();
        wheelLines = Native.WheelLines();
        // Reading direction comes from the UI language. Arabic, Hebrew, Persian and
        // Urdu write right to left, and every Windows shell mirrors its layout for
        // them; a card that did not would be the one window facing the other way.
        rtl = IsRightToLeft();
        surface = dark ? Color.FromArgb(0x2B, 0x2B, 0x2E) : SystemColors.Window;
        titleColor = dark ? Color.FromArgb(0xF2, 0xF2, 0xF5) : Color.FromArgb(0x1B, 0x1B, 0x1F);
        bodyColor = dark ? Color.FromArgb(0xC8, 0xC8, 0xCE) : Color.FromArgb(0x44, 0x44, 0x4A);
        mutedColor = dark ? Color.FromArgb(0x92, 0x92, 0x9C) : Color.FromArgb(0x8A, 0x8A, 0x92);
        // Dark surfaces need a lighter border to read as an edge at all.
        hairlineColor = dark ? Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
        hoverColor = dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00);
        trackColor = dark ? Color.FromArgb(0x3A, 0x3A, 0x40) : Color.FromArgb(0xD8, 0xD8, 0xDC);
        thumbColor = dark ? Color.FromArgb(0x6A, 0x6A, 0x74) : Color.FromArgb(0xA8, 0xA8, 0xB0);

        // High contrast outranks the theme: it is the user telling Windows that no
        // palette it did not choose is readable. The card then borrows the system
        // colours wholesale, so it matches whatever scheme is loaded instead of
        // guessing at a variant of the one above.
        if (contrast)
        {
            surface = SystemColors.Window;
            titleColor = SystemColors.WindowText;
            bodyColor = SystemColors.WindowText;
            mutedColor = SystemColors.GrayText;
            hairlineColor = SystemColors.WindowText;
            hoverColor = SystemColors.Control;
            trackColor = SystemColors.ControlDark;
            thumbColor = SystemColors.ControlDarkDark;
        }

        string ui = PickUiFont();

        // The whole layout is written in the 420-unit grid the card was designed
        // on; `unit` is what one of those units is worth on this screen. Width
        // follows the monitor, everything else follows the width, so a 1366px
        // laptop gets the same design shrunk down instead of a card covering a
        // third of the desktop. Text has a floor: below it the card stops being
        // readable, and spreading over one more pixel is the better trade.
        //
        // Two user settings move the floor. "Make text bigger" raises the type
        // and then the whole card with it, so the design stays in proportion
        // instead of turning into cramped large text; high contrast has to fit
        // too, because a reflowed card with a scrollbar is worse than a wider one.
        float textScale = Native.TextScale();
        int deviceWidth = (int)Math.Round(area.Width * 0.20 * textScale);
        deviceWidth = Math.Max((int)Math.Round(330 * scale * textScale), Math.Min((int)Math.Round(560 * scale * textScale), deviceWidth));
        // The layout grid stays a true ratio - it has to, since the design is 420
        // units wide and the window is `deviceWidth` pixels - but every coordinate
        // derived from it is snapped to a whole device pixel, and the two font
        // sizes below are whole pixels as well. Those are the parts that decide
        // whether glyphs sit on the grid or between its pixels.
        unit = deviceWidth / 420f;
        // Integer pixel sizes only. GDI rasterizes a hinting-friendly em size;
        // 18.29px asks for an em no font was hinted for and the stems come out at
        // inconsistent widths from one glyph to the next.
        int textPx = Math.Max(15, (int)Math.Round(15 * unit, MidpointRounding.AwayFromZero));

        // Whole device pixels, not points: 11pt at 120dpi is 18.33px and that
        // fractional em size lands glyph stems off the pixel grid.
        captionFont = new Font(ui, textPx, FontStyle.Bold, GraphicsUnit.Pixel);
        bodyFont = new Font(ui, textPx, FontStyle.Regular, GraphicsUnit.Pixel);
        // The footer is the smallest type on the card, which is exactly where a
        // fractional em hurts most: at 13px a half-pixel of stem drift is a whole
        // stroke. It stays on a whole pixel too, and never below 12px.
        int metaPx = Math.Max(12, (int)Math.Round(12 * unit, MidpointRounding.AwayFromZero));
        metaFont = new Font(ui, metaPx, FontStyle.Regular, GraphicsUnit.Pixel);

        // The bottom of the card is a stack of short rows: the timestamp and the
        // session, and the permission name under it. The card is exactly as tall
        // as it has rows, so a host that reports neither gets the compact card
        // back instead of a blank strip.
        bool named = opt.Preset.Length != 0;
        int rows = (named ? 1 : 0) + 1;
        int logicalHeight = 148 + rows * 24 + 6;

        AutoScaleMode = AutoScaleMode.None;
        Text = opt.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = surface;
        Size = new Size(deviceWidth, Px(logicalHeight));
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // Both edges are flush: a bottom corner sits flat on the taskbar, a top
        // corner is level with the top of the screen. `area` is the working area,
        // so the taskbar is already excluded from it.
        targetX = leftCorner ? area.Left : area.Right - Width;
        // Cards overlap instead of tiling: every card except the front one keeps
        // only this strip visible past the card in front of it, enough to read
        // its title and reach its close button. The deck runs away from the
        // anchored edge, so a top corner stacks downward and a bottom one upward.
        slotOffset = Px(44);
        slotBase = topCorner ? area.Top : area.Bottom - Height;

        cardDir = Path.Combine(Path.GetTempPath(), "dsh-notice-cards");
        try { Directory.CreateDirectory(cardDir); } catch { }
        // The corner is part of the file name, not just the window position: each
        // corner is its own deck. Sharing one would let a bottom-left card count
        // the cards in a top-right stack and land in a slot no one can see.
        cardFile = Path.Combine(cardDir, string.Format("{0:D19}-{1}-{2}.card", DateTime.Now.Ticks, Process.GetCurrentProcess().Id, cornerKey));

        targetY = SlotY();
        Location = new Point(targetX, targetY + (topCorner ? -Px(28) : Px(28)));
        Opacity = 0;

        // Rounded corners: a Region clips everything drawn, including the border,
        // so the shape stays consistent at every size. 8px is the toast radius.
        shape = Round(new Rectangle(0, 0, Width, Height), Px(8));
        Region = new Region(shape);

        // Both text blocks are hand-painted rather than living in a Label, because
        // a Label brings its own padding, its own font inheritance and its own
        // redraw policy. What is painted is GDI ClearType (see PaintBody), so the
        // wrapping is measured with the same engine that draws it.
        bodyWidth = Px(372);
        bodyHeight = Px(92);
        barWidth = Px(8);
        // One whole pixel per line, from GDI's own line spacing. A fractional line
        // height accumulated over ten lines is a whole pixel of drift by the
        // bottom of the card, and every line lands a little further off the grid.
        lineHeight = Math.Max(1, TextRenderer.MeasureText("Ag中", bodyFont,
            new Size(int.MaxValue, int.MaxValue), TextFormats).Height);
        bodyLines = SplitLines(opt.Body, bodyFont, bodyWidth);
        int totalHeight = bodyLines.Count * lineHeight;
        maxScroll = Math.Max(0, totalHeight - bodyHeight);
        thumbHeight = Math.Max(Px(20), (int)((long)bodyHeight * bodyHeight / Math.Max(1, totalHeight)));

        // The title sits in its own panel so a click anywhere on it still dismisses.
        // GDI text again (see PaintBody): the title is the most looked-at line on
        // the card, so it gets the same ClearType treatment as the body rather
        // than a fat greyscale smear.
        BufferedPanel caption = new BufferedPanel();
        caption.Location = new Point(Px(20), Px(18));
        caption.Size = new Size(Px(320), Px(26));
        caption.BackColor = surface;
        caption.Paint += delegate(object s, PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, opt.Title, captionFont, new Point(0, 0), titleColor, TextFormats);
        };
        caption.Click += delegate { BeginExit(); };
        Controls.Add(caption);

        // The body scrolls instead of running off the bottom. WinForms controls
        // cannot be scrolled without keyboard focus and this card deliberately
        // never takes focus, so the offset is ours: the wheel adjusts it, and the
        // thumb can be dragged, which works because plain mouse messages do reach
        // the card.
        bodyBox = new BufferedPanel();
        bodyBox.Location = new Point(Px(20), Px(48));
        bodyBox.Size = new Size(bodyWidth + barWidth, bodyHeight);
        bodyBox.BackColor = surface;
        bodyBox.Paint += delegate(object s, PaintEventArgs e) { PaintBody(e.Graphics); };
        bodyBox.MouseWheel += delegate(object s, MouseEventArgs e)
        {
            // Delta is negative when the wheel rolls down, and rolling down moves
            // the text up, so the sign is flipped. The number of rows per notch is
            // the user's mouse setting, not a constant.
            double step = -e.Delta / 120.0 * lineHeight * wheelLines;
            scrollTarget = Math.Max(0.0, Math.Min((double)maxScroll, scrollTarget + step));
            gliding = true;
            // The pump stops itself once the entrance animation and any glide are
            // done, so a later wheel has to bring it back for the easing frames.
            if (settled) StartPump(4);
        };
        bodyBox.MouseDown += delegate(object s, MouseEventArgs e)
        {
            if (e.X >= bodyWidth && maxScroll > 0)
            {
                dragging = true;
                // Grab the thumb where it was clicked, so it does not jump under
                // the cursor on the first move.
                double thumbY = (bodyHeight - thumbHeight) * scroll / maxScroll;
                dragGrab = e.Y - thumbY;
            }
        };
        bodyBox.MouseUp += delegate { dragging = false; };
        bodyBox.MouseMove += delegate(object s, MouseEventArgs e)
        {
            if (dragging && maxScroll > 0)
            {
                double travel = Math.Max(1, bodyHeight - thumbHeight);
                scroll = Math.Max(0.0, Math.Min((double)maxScroll, (e.Y - dragGrab) * maxScroll / travel));
                scrollTarget = scroll;
                bodyBox.Invalidate();
            }
        };
        Controls.Add(bodyBox);

        // The two corner buttons are fingertip-sized on a touch screen: the shell's
        // own guidance is 23px at 100% for a mouse and 34+ for a finger, and a
        // fingertip has no hover to confirm it hit. The right margin and the gap
        // between the two stay as designed; only the button grows leftward.
        //
        // A right-to-left interface puts its window controls on the left, so the
        // pair swaps sides there. The glyphs themselves are direction-free.
        int iconSize = touch ? 36 : 28;
        int iconGap = 4;
        int iconMargin = 4;
        int iconY = touch ? 8 : 12;
        int iconRightLeft = rtl ? iconMargin : 420 - iconMargin - iconSize;
        int iconLeftLeft = rtl ? iconMargin + iconSize + iconGap : iconRightLeft - iconGap - iconSize;
        AddIconButton(iconLeftLeft, iconY, iconSize, DrawWindowGlyph, delegate { FocusApp(); BeginExit(); });
        AddIconButton(iconRightLeft, iconY, iconSize, DrawCloseGlyph, delegate { BeginExit(); });

        // The footer names the moment and the session, then the permission mode.
        // Two strips rather than one label: a long session name truncates on its
        // own without pushing the clock around.
        //
        // The clock is written in the user's own short-time pattern, so a 12-hour
        // region sees "2:35 PM" and a 24-hour one sees "14:35" - the same string
        // the taskbar shows - instead of one format imposed on everyone.
        int metaY = 148;
        // The permission line is a read-only echo of the client's dropdown: the name
        // only. Switching it from here would change the sandbox under a running
        // tool call, and that aborts the call.
        //
        // In a right-to-left interface the clock takes the far edge and the session
        // name the near one: the line mirrors with the rest of the layout instead
        // of keeping one end pinned to the same screen side.
        if (rtl)
        {
            AddFooterRow(metaY, 420 - 20 - 200, 200, DateTime.Now.ToString("t", CultureInfo.CurrentCulture), true);
            if (opt.Session.Length != 0) AddFooterRow(metaY, 20, 174, opt.Session, false);
            if (named) AddFooterRow(metaY + 24, 20, 380, opt.Preset, true);
        }
        else
        {
            AddFooterRow(metaY, 20, 200, DateTime.Now.ToString("t", CultureInfo.CurrentCulture), false);
            if (opt.Session.Length != 0) AddFooterRow(metaY, 226, 174, opt.Session, true);
            if (named) AddFooterRow(metaY + 24, 20, 380, opt.Preset, false);
        }

        life = new Timer();
        life.Interval = Math.Max(1, opt.Seconds) * 1000;
        life.Tick += delegate { life.Stop(); BeginExit(); };

        // Every card re-derives its slot, because closing the front card promotes
        // the next one to the fully drawn corner.
        reflow = new Timer();
        reflow.Interval = 300;
        reflow.Tick += delegate { Reflow(); };

        // Approval and question cards mirror the in-app prompt: the plugin
        // creates the dismiss file when the request resolves. That alone must not
        // close the card, because a request can be settled with nobody at the
        // desk, and the notice would then vanish unseen while the conversation
        // sits there waiting. So the card also asks whether DSH was in front, and
        // leaves only when the answer and that sighting belong to the same
        // moment.
        if (opt.DismissFile.Length != 0)
        {
            clock = Stopwatch.StartNew();
            watch = new Timer();
            watch.Interval = 100;
            watch.Tick += delegate { WatchTick(); };
        }

        frame = delegate
        {
            if (exiting) { ExitFrame(); return; }
            if (!settled) { EntranceFrame(); return; }
            if (gliding) GlideFrame();
        };
    }

    // A notice must never take the keyboard. ShowWithoutActivation keeps
    // ShowWindow from pulling focus onto the card, and NOACTIVATE keeps it that
    // way when the shell re-evaluates the window. TOOLWINDOW is deliberately off:
    // a tool window does not take part in normal mouse hit-testing reliably.
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;          // WS_EX_NOACTIVATE
            cp.ExStyle &= ~0x00000080;         // no WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_FRAME)
        {
            Action f = frame;
            if (f != null) { try { f(); } catch { } }
            return;
        }
        base.WndProc(ref m);
    }

    // Hairline border only; the shell draws no accent stripe on its toasts.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen pen = new Pen(hairlineColor))
            e.Graphics.DrawPath(pen, shape);
    }

    // One design unit is one pixel of the 420-unit grid at the scale this screen
    // settled on, so every coordinate written below scales with the card instead
    // of staying pinned to 96dpi.
    //
    // AwayFromZero rather than the default banker's rounding: Math.Round(0.5)
    // goes to the even number, so a run of coordinates that all land on .5 comes
    // out as 8,8,8,10,10,10 instead of 8,9,10,11 - visibly uneven spacing along
    // an edge that was designed as an arithmetic progression.
    int Px(double v) { return (int)Math.Round(v * unit, MidpointRounding.AwayFromZero); }
    float Pf(double v) { return (float)Math.Round(v * unit, MidpointRounding.AwayFromZero); }

    // --- deck ---

    // Slot 0 is the oldest card alive and owns the front of the deck: the fully
    // drawn, anchored position. Every later card is tucked behind it and one step
    // further from the anchored edge. A file whose owning process is gone is
    // swept here too, so the stack cannot grow phantom slots.
    int SlotY()
    {
        List<string> live = new List<string>();
        string[] files;
        // Only the cards of this deck: a bottom-left card must not count a
        // top-right stack and land in a slot nobody can see.
        try { files = Directory.GetFiles(cardDir, "*-" + cornerKey + ".card"); }
        catch { files = new string[0]; }
        int self = Process.GetCurrentProcess().Id;
        foreach (string f in files)
        {
            string[] parts = Path.GetFileNameWithoutExtension(f).Split('-');
            int owner = 0;
            if (parts.Length >= 2) int.TryParse(parts[1], out owner);
            if (owner != 0 && owner != self && !Native.ProcessAlive(owner))
            {
                try { File.Delete(f); } catch { }
                continue;
            }
            live.Add(f);
        }
        live.Sort(StringComparer.Ordinal);

        // The deck is capped, and the card that has to go is the one at the far
        // end - the oldest. Retiring it here, from the card that just arrived,
        // keeps the cap honest across processes: every arriving card makes the
        // same calculation from the same directory, so exactly the surplus is
        // closed once. A card closing itself would take the newest instead, which
        // is the one the user has not seen yet.
        int limit = StackLimit(opt.MaxStack, area, Height, slotOffset);
        while (live.Count > limit)
        {
            string oldest = live[0];
            try
            {
                long hwnd;
                if (long.TryParse(File.ReadAllText(oldest).Trim(), out hwnd) && hwnd != 0)
                {
                    IntPtr h = new IntPtr(hwnd);
                    if (Native.IsWindow(h)) Native.ForceClose(h);
                }
            }
            catch { }
            try { File.Delete(oldest); } catch { }
            live.RemoveAt(0);
        }

        int index = 0;
        for (int i = 0; i < live.Count; i++) if (live[i] == cardFile) { index = i; break; }

        prevHwnd = IntPtr.Zero;
        if (index > 0)
        {
            try
            {
                string raw = File.ReadAllText(live[index - 1]).Trim();
                long prev;
                if (long.TryParse(raw, out prev) && prev != 0) prevHwnd = new IntPtr(prev);
            }
            catch { }
        }
        if (topCorner)
            return Math.Min(area.Bottom - Height, slotBase + index * slotOffset);
        return (int)Math.Max(area.Top, slotBase - index * slotOffset);
    }

    // A card over a full-screen app must not be a mouse trap. The rectangle it
    // covers is a rectangle the app below can no longer receive the cursor in,
    // and over a video that reads as a dead patch in the corner. WS_EX_TRANSPARENT
    // makes hit-testing skip this window entirely, so the cursor and every click
    // go straight to the app underneath.
    //
    // Only while something owns the whole screen. On a normal desktop there is no
    // conflict, and there the card stays clickable so it can be closed or
    // scrolled. Checked from the reflow tick as well, because the foreground app
    // can change while the card is up.
    void ApplyClickThrough()
    {
        bool want = Native.FullScreenForeground();
        if (want == clickThrough) return;
        clickThrough = want;
        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        ex &= ~Native.WS_EX_TOOLWINDOW;
        ex |= Native.WS_EX_NOACTIVATE;
        if (want)
        {
            // Hit-testing only skips a transparent window when it is layered as
            // well; WS_EX_TRANSPARENT on its own is a painting hint, not a mouse
            // one. The alpha is set explicitly because a layered window without
            // attributes is invisible.
            ex |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
            Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        }
        else
        {
            ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
            // Leave the card opaque: Opacity takes it from here.
            Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        }
    }

    void Reflow()
    {
        if (!settled) return;
        ApplyClickThrough();
        int y = SlotY();
        if (Location.Y != y) Location = new Point(targetX, y);
        // Z-order is not raised from here - a raise is a one-shot event and the
        // last card to fire would win, which is exactly the race that put the
        // newest card in front. Instead each card re-inserts itself directly
        // below the card one step older than it, so the whole deck comes out in
        // age order regardless of which timer fires first.
        IntPtr after = (prevHwnd == IntPtr.Zero || !Native.IsWindow(prevHwnd)) ? IntPtr.Zero : prevHwnd;
        Native.SetWindowPos(Handle, after, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOMOVE);
    }

    // --- timers ---

    void WatchTick()
    {
        long now = clock.ElapsedMilliseconds;
        if (!answered && File.Exists(opt.DismissFile))
        {
            answered = true;
            answerAt = now;
            // Consumed, so the marker is not left behind for the next reader of
            // the temp directory. The plugin keeps its own timer as the backstop
            // for a card that never got far enough to read it.
            try { File.Delete(opt.DismissFile); } catch { }
        }
        // Without a process name there is no way to tell "the reader is back", so
        // the answer is the only signal available and the card leaves on it.
        if (opt.ProcessName.Length == 0)
        {
            if (answered) BeginExit();
            return;
        }
        // Reading the foreground window is cheap, so it is sampled every tick:
        // the moment that matters is the one right around the answer, and a
        // one-second poll can step right over it.
        if (Native.ForegroundIs(opt.ProcessName)) lastSeen = now;
        // A card whose DSH is gone has nothing left to unblock, and a window with
        // no owner never gets dismissed by anybody. Leaving with the app is the
        // one exit that does not depend on the reader seeing it. The process
        // table is the expensive query, hence once a second.
        ownerTick++;
        if (ownerTick >= 10)
        {
            ownerTick = 0;
            if (opt.OwnerPid > 0 && !Native.ProcessAlive(opt.OwnerPid)) { BeginExit(); return; }
        }
        // Both halves or nothing: the request is answered, and DSH was in front
        // either as the answer landed or after it. Never-seen is -1, which
        // floor-clears the arithmetic, so that case is excluded by hand.
        if (!answered) return;
        if (lastSeen >= 0 && lastSeen >= answerAt - SeenGraceMs) BeginExit();
    }

    // --- painting ---

    // GDI (TextRenderer), not GDI+ (Graphics.DrawString). This is the single
    // biggest difference in how the card reads: GDI+ only ever anti-aliases in
    // greyscale, so every glyph edge is a two-or-three pixel grey smear and the
    // text looks swollen and soft no matter the font. GDI asks the system text
    // stack for ClearType, which is what every native Windows surface uses, and
    // its shaping lands on the pixel grid instead of between pixels.
    //
    // ClearType needs an opaque backdrop to work on - it assumes the destination
    // pixels it blends into - so the body panel paints its own background first.
    // Shared flags for every GDI text call on the card. NoPadding is what keeps
    // GDI from adding its own leading and shifts the glyphs onto the layout grid
    // the coordinates below assume; NoPrefix keeps a literal "&" out of
    // accelerator handling; SingleLine because every string is pre-wrapped.
    const TextFormatFlags TextFormats = TextFormatFlags.NoPadding
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.SingleLine
        | TextFormatFlags.PreserveGraphicsClipping;

    void PaintBody(Graphics g)
    {
        // Draw from the first line that is partly visible, offset by the
        // fractional remainder, so the text moves by whole pixels instead of
        // jumping a row.
        int first = Math.Max(0, (int)Math.Floor(scroll / lineHeight));
        int offset = (int)Math.Round(scroll - first * lineHeight);
        int last = Math.Min(bodyLines.Count, first + (int)Math.Ceiling((double)bodyHeight / lineHeight) + 1);
        for (int i = first; i < last; i++)
        {
            int y = (i - first) * lineHeight - offset;
            TextRenderer.DrawText(g, bodyLines[i], bodyFont, new Point(0, y), bodyColor, TextFormats);
        }
        if (maxScroll > 0)
        {
            // A hairline track with a proportionally sized thumb, like a real bar.
            using (SolidBrush t = new SolidBrush(trackColor))
                g.FillRectangle(t, new Rectangle(bodyWidth + Px(3), 0, Px(2), bodyHeight));
            int thumbY = (int)((bodyHeight - thumbHeight) * scroll / maxScroll);
            using (SolidBrush t = new SolidBrush(thumbColor))
                g.FillRectangle(t, new Rectangle(bodyWidth + Px(2), thumbY, Px(4), thumbHeight));
        }
    }

    void DrawWindowGlyph(Graphics g, int w, int h)
    {
        float cw = Pf(13), ch = Pf(11);
        float x = (w - cw) / 2, y = (h - ch) / 2;
        using (Pen pen = new Pen(titleColor, Pf(1.2)))
        {
            g.DrawRectangle(pen, x, y, cw, ch);
            float rule = (float)(y + ch * 0.3);
            g.DrawLine(pen, x, rule, x + cw, rule);
        }
    }

    void DrawCloseGlyph(Graphics g, int w, int h)
    {
        float s = Pf(5), cx = w / 2f, cy = h / 2f;
        using (Pen pen = new Pen(titleColor, Pf(1.2)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
            g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
        }
    }

    // Two small icon buttons in the top-right corner. The glyphs are drawn with
    // GDI+ rather than taken from an icon font so nothing depends on which fonts
    // are installed, and every coordinate is scaled: the button is sized in
    // scaled pixels, so an unscaled glyph would sit off-centre at anything but
    // 100%.
    void AddIconButton(int left, int top, int size, Action<Graphics, int, int> draw, EventHandler onClick)
    {
        BufferedPanel p = new BufferedPanel();
        p.Location = new Point(Px(left), Px(top));
        p.Size = new Size(Px(size), Px(size));
        p.BackColor = surface;
        p.Cursor = Cursors.Hand;
        p.TabStop = false;
        bool hover = false;
        p.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover)
            {
                // Rounded like the card itself; a square hover block would poke
                // out past the card's corner radius and read as a stray grey box.
                int r = Px(5);
                using (GraphicsPath hp = new GraphicsPath())
                {
                    hp.AddArc(0, 0, r * 2, r * 2, 180, 90);
                    hp.AddArc(p.Width - r * 2, 0, r * 2, r * 2, 270, 90);
                    hp.AddArc(p.Width - r * 2, p.Height - r * 2, r * 2, r * 2, 0, 90);
                    hp.AddArc(0, p.Height - r * 2, r * 2, r * 2, 90, 90);
                    hp.CloseAllFigures();
                    using (SolidBrush fill = new SolidBrush(hoverColor))
                        e.Graphics.FillPath(fill, hp);
                }
            }
            draw(e.Graphics, p.Width, p.Height);
        };
        p.MouseEnter += delegate { if (!touch) { hover = true; p.Invalidate(); } };
        p.MouseLeave += delegate { if (hover) { hover = false; p.Invalidate(); } };
        p.Click += onClick;
        Controls.Add(p);
    }

    // A footer row is a bare strip of paint: no border, no background, and it
    // never swallows a click, so the card underneath still dismisses. `right`
    // aligns the text to the strip's far edge, which is what the session name
    // wants: it is the variable-length half of the line.
    void AddFooterRow(int top, int left, int width, string text, bool right)
    {
        BufferedPanel row = new BufferedPanel();
        row.Location = new Point(Px(left), Px(top));
        row.Size = new Size(Px(width), Px(20));
        row.BackColor = surface;
        row.Paint += delegate(object s, PaintEventArgs e)
        {
            // GDI, not GDI+: this is a single short line with a hard width, which
            // is exactly what TextRenderer does well, and it puts the glyphs where
            // the system's own text layout puts them. Ellipsis trimming is on so a
            // long session name ends in "..." instead of running under the buttons.
            TextFormatFlags flags = TextFormats
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.VerticalCenter
                | (right ? TextFormatFlags.Right : TextFormatFlags.Left);
            // The paint surface, not the control's own Size: with per-monitor DPI
            // the two disagree, and a right edge taken from Size puts the text
            // outside the surface, where it simply is not drawn.
            RectangleF clip = e.Graphics.VisibleClipBounds;
            int boxWidth = (int)Math.Round(clip.Width);
            int boxHeight = (int)Math.Round(clip.Height);
            Rectangle box = new Rectangle(0, 0, boxWidth, boxHeight);
            TextRenderer.DrawText(e.Graphics, text, metaFont, box, mutedColor, flags);
        };
        row.Click += delegate { BeginExit(); };
        Controls.Add(row);
    }

    static GraphicsPath Round(Rectangle r, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, radius * 2, radius * 2, 180, 90);
        path.AddArc(r.Right - radius * 2, r.Top, radius * 2, radius * 2, 270, 90);
        path.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(r.Left, r.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseAllFigures();
        return path;
    }

    // Measures with GDI, because GDI is what draws. GDI+ and GDI do not agree on
    // the width of the same string - different shaping, different hinting - so
    // measuring with one and drawing with the other either wraps a line that had
    // room or clips one that did not. `TextFormatFlags.NoPadding` matches the
    // flags the painter uses, so the two see exactly the same metrics.
    static int TextWidth(string text, Font font)
    {
        return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormats).Width;
    }

    // Word wrap and clipping for the hand-painted body. A plain DrawString
    // ignores the layout rectangle's right edge, so the text is broken into lines
    // first and the painter stops at the first line that would overflow.
    //
    // Chinese has no spaces, so splitting on spaces yields one enormous "word"
    // and nothing ever wraps. A token that cannot fit on a line of its own is
    // therefore broken down character by character, which is also the correct
    // rule for CJK text.
    //
    // Characters that may not open a line (kinsoku shori): a closing mark left at
    // the start of the next line reads as a typo, so it stays on the line it
    // closes even if that line then runs a hair past the measure.
    static List<string> SplitLines(string text, Font font, int width)
    {
        const string noLineStart = "。，、；：？！）】》」』〉·％,.;:?!)]}";
        List<string> lines = new List<string>();
        string[] paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0) { lines.Add(""); continue; }
            string current = "";
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (TextWidth(candidate, font) <= width) { current = candidate; continue; }
                if (current.Length != 0) { lines.Add(current); current = ""; }
                if (TextWidth(word, font) <= width) { current = word; continue; }
                string chunk = "";
                foreach (char ch in word)
                {
                    string grown = chunk + ch;
                    if (chunk.Length != 0 && TextWidth(grown, font) > width)
                    {
                        if (noLineStart.IndexOf(ch) >= 0) { lines.Add(grown); chunk = ""; }
                        else { lines.Add(chunk); chunk = ch.ToString(); }
                    }
                    else chunk = grown;
                }
                current = chunk;
            }
            if (current.Length != 0) lines.Add(current);
        }
        return lines;
    }

    // --- animation ---

    void StartPump(int ms)
    {
        StopPump();
        pumping = true;
        pumpTarget = Handle;
        Thread t = new Thread(delegate()
        {
            while (pumping)
            {
                if (!Native.IsWindow(pumpTarget)) break;
                Native.PostFrame(pumpTarget);
                Thread.Sleep(ms);
            }
        });
        t.IsBackground = true;
        pump = t;
        t.Start();
    }

    void StopPump()
    {
        pumping = false;
        Thread t = pump;
        pump = null;
        if (t != null) { try { t.Join(200); } catch { } }
    }

    // Slide in from the anchored edge and fade in, ease-out cubic so it settles
    // instead of stopping dead, with a small overshoot (ease-out back) so the card
    // reads as landing rather than being dragged into place. The travel is
    // downward from a top corner and upward from a bottom one.
    void EntranceFrame()
    {
        // With animations switched off in Windows the card does not slide: it
        // appears in its slot, fully opaque, on the first frame.
        if (!animated)
        {
            Opacity = 1.0;
            Location = new Point(targetX, targetY);
            SettleStyles();
            settled = true;
            StopPump();
            return;
        }
        double t = Math.Min(1.0, entranceSw.Elapsed.TotalMilliseconds / 420.0);
        double ease = 1 - Math.Pow(1 - t, 3);
        double c1 = 1.70158, c3 = c1 + 1;
        double back = 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
        Opacity = ease;
        int from = (int)Math.Round((topCorner ? -1 : 1) * Px(36) * (1 - back));
        Location = new Point(targetX, targetY + from);
        if (t < 1.0) return;
        SettleStyles();
        settled = true;
        if (!gliding) { StopPump(); return; }
    }

    // WinForms rewrites the extended styles while animating Opacity; put
    // NOACTIVATE back (and keep TOOLWINDOW off) so the card keeps ignoring
    // focus but still accepts clicks for the rest of its life.
    void SettleStyles()
    {
        int ex = Native.GetWindowLong(Handle, -20);
        ex &= ~0x00000080;
        Native.SetWindowLong(Handle, -20, ex | 0x08000000);
    }

    // Exit: fade out while sliding back toward the anchored edge, ease-in cubic so
    // it leaves slowly and then accelerates away. Every close path funnels here.
    void ExitFrame()
    {
        if (!animated)
        {
            StopPump();
            Close();
            return;
        }
        double t = Math.Min(1.0, exitSw.Elapsed.TotalMilliseconds / 220.0);
        double ease = Math.Pow(t, 3);
        Opacity = exitFromOpacity * (1 - ease);
        Location = new Point(targetX, (int)Math.Round(exitFromY + (topCorner ? -1 : 1) * Px(24) * ease));
        if (t >= 1.0)
        {
            StopPump();
            Close();
        }
    }

    void GlideFrame()
    {
        double delta = scrollTarget - scroll;
        if (!animated) { scroll = scrollTarget; gliding = false; StopPump(); bodyBox.Invalidate(); return; }
        if (Math.Abs(delta) < 0.5)
        {
            scroll = scrollTarget;
            gliding = false;
            StopPump();
        }
        else scroll += delta * 0.28;
        bodyBox.Invalidate();
    }

    // The exit animation owns the window position and opacity, so the timers stop
    // first - a reflow tick mid-fade would fight the slide.
    void BeginExit()
    {
        if (exiting) return;
        exiting = true;
        // The card is on its way out under its own power, so the rescue has
        // nothing left to rescue. What still needs a deadline is the exit: a card
        // that waits for an answer is armed with none, and the fade is the one
        // thing that can now leave it on screen forever.
        Watchdog.DisarmSettle();
        if (opt.DismissFile.Length != 0) Watchdog.ArmDeadline(5000);
        life.Stop();
        reflow.Stop();
        if (watch != null) watch.Stop();
        exitFromOpacity = Opacity;
        exitFromY = Location.Y;
        exitSw = Stopwatch.StartNew();
        StartPump(4);
    }

    // --- window ---

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        IntPtr h = Handle;
        // Everything that keeps a card from getting stuck is started first,
        // before anything that can throw. The stopwatch is armed before the pump
        // so the very first frame already has a start time to measure against.
        entranceSw = Stopwatch.StartNew();
        StartPump(4);
        // An approval card has no life timer: it is supposed to stay until the
        // answer arrives, and only the dismiss file ends it (with the watchdog as
        // backstop).
        if (opt.DismissFile.Length == 0) life.Start();
        reflow.Start();
        if (watch != null) watch.Start();
        try
        {
            // The card's own handle is what the deck ordering is anchored on, so
            // it is published as soon as it exists. Best effort: losing the slot
            // costs this card its stacking order, and that is not worth a card
            // that stays up forever because the write threw.
            try
            {
                Directory.CreateDirectory(cardDir);
                File.WriteAllText(cardFile, h.ToInt64().ToString());
            }
            catch { }
            // Re-assert the styles on the real handle: TopMost alone can be beaten
            // by a shell-owned full-screen window.
            int ex = Native.GetWindowLong(h, -20);
            ex &= ~0x00000080;
            Native.SetWindowLong(h, -20, ex | 0x00000008 | 0x08000000);
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOMOVE);
        }
        catch { }
    }

    void FocusApp()
    {
        // SW_RESTORE first: a minimized DSH has a live handle that
        // SetForegroundWindow alone will not bring back.
        if (opt.ProcessName.Length != 0)
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName(opt.ProcessName))
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        Native.ShowWindow(p.MainWindowHandle, 9);
                        Native.SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                }
            }
            catch { }
        }
        if (opt.AppPath.Length != 0 && File.Exists(opt.AppPath))
        {
            try { Process.Start(opt.AppPath); } catch { }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Watchdog.Disarm();
        StopPump();
        life.Dispose();
        reflow.Dispose();
        if (watch != null) watch.Dispose();
        // Leaving the slot file behind would keep this card's place in the stack
        // for good, so the next card would never reclaim the corner.
        try { File.Delete(cardFile); } catch { }
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Options opt = Options.Parse(args);
        Native.SetProcessDpiAwarenessContext(-4);
        // WinForms timers ride the system timer tick, which defaults to ~15.6ms
        // no matter what Interval says. Raising the global timer resolution to 1ms
        // is what lets the entrance animation actually hit its interval. Released
        // at the end so the card does not tax the whole desktop while it lives.
        Native.timeBeginPeriod(1);

        // A card that never appears is a lost notice, so the wait for an
        // exclusive-fullscreen app is bounded rather than skipped: one retry
        // window of 2s covers a game that is closing or a user who alt-tabs out,
        // and after that the card is shown anyway.
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(2000);
        while (Native.ExclusiveFullScreen() && DateTime.UtcNow < deadline)
        {
            if (opt.DismissFile.Length != 0 && File.Exists(opt.DismissFile)) break;
            Thread.Sleep(250);
        }
        // Nothing left to say if the question was answered while it waited.
        if (opt.DismissFile.Length != 0 && File.Exists(opt.DismissFile))
        {
            Native.timeEndPeriod(1);
            return 0;
        }
        // Quiet hours (the user's focus session, presentation mode, a full-screen
        // app) suppress interruptions. A card with no dismiss file is a turn-end
        // notice, i.e. an interruption, and is dropped; a card that carries one is
        // the conversation waiting on a human, which is not an interruption.
        if (opt.DismissFile.Length == 0 && Native.QuietTime())
        {
            Native.timeEndPeriod(1);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // A card runs its own event handlers, and an exception escaping one of
        // them would surface as the WinForms unhandled-exception dialog. That
        // dialog is modal, so the card never closes: swallowing here turns a
        // wedged card into one that merely misbehaves for a frame.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate { };

        Card card = new Card(opt);
        // Armed before the window is shown, and off the message loop, so it
        // survives the cases that matter: a Shown handler that never finishes and
        // a pump that never runs. Those are exactly what leaves a card up and
        // invisible for good. The slot it aims at is the one the entrance
        // animation lands on, so that is where a rescue puts it back.
        int hardMs = opt.DismissFile.Length != 0 ? 0 : Math.Max(1, opt.Seconds) * 1000 + 3000;
        Watchdog.Arm(card.Handle, card.SlotX, card.SlotYTarget, 1000, hardMs);

        Application.Run(card);

        Native.timeEndPeriod(1);
        return 0;
    }
}

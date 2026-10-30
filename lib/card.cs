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
using System.Drawing.Imaging;
using System.Drawing.Text;
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

    // The two surfaces the Settings app itself draws on, sampled off a live
    // window: a dark card sits at #2B2B2B with #FFFFFF text and #CFCFCF
    // secondary text, a light one at #FFFFFF with #1A1A1A text. Both are flat
    // colours on purpose - Settings never tints them, so neither may this.
    //
    // SystemColors is the obvious answer and the wrong one: it is the classic GDI
    // palette, nailed to a white window and black text whatever the user picked,
    // so a dark card built from it comes out light. uxtheme was the previous
    // answer, and it is not this either: ImmersiveApplicationBackground is the
    // solid shell black (#000000), not the elevated surface a Settings card is
    // painted with, so a card built from it reads as a hole in the desktop
    // instead of as a panel lying on it.
    public static bool ShellInk(bool dark, out Color back, out Color fore)
    {
        if (dark)
        {
            back = Color.FromArgb(0x2B, 0x2B, 0x2B);
            fore = Color.FromArgb(0xFF, 0xFF, 0xFF);
        }
        else
        {
            back = Color.FromArgb(0xFF, 0xFF, 0xFF);
            fore = Color.FromArgb(0x1A, 0x1A, 0x1A);
        }
        return true;
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
    // Type size as a percentage of the designed card: 100 leaves it alone, and
    // anything else grows or shrinks the glyphs only. The card keeps its width,
    // its height and every band's position on it, and the line spacing inside the
    // body keeps following the type: bigger words mean fewer lines visible in the
    // same box, which the body scrolls. A card that grew with its type would be
    // `Scale`, and would move the neighbours' slots every time someone nudged it.
    public int FontSize = 100;
    // Which rung of a preview stack this card is, 0 being the fully drawn front
    // one. The settings page raises one card per rung so that "cards per corner"
    // can be looked at instead of imagined; a real notice never sets this.
    public int PreviewSlot = 0;
    // What this preview card is showing: "turnEnd", "approval" or "question".
    // Empty means the blank card the page falls back to when every kind is off.
    // Only a preview uses it, and only so the page can tell which card was closed.
    public string PreviewKind = "";
    // A preview card that stands for nothing: the page has every kind switched
    // off and still owes the reader a stack, so it shows the shape of a card with
    // no words in it rather than nothing at all. It is labelled by its position in
    // the stack and its clock is staggered, so three identical blanks can still be
    // told apart.
    public bool Blank = false;
    // Minutes added to this card's footer clock. Only a preview uses it: the stack
    // is drawn all at once, and three cards stamped the same minute look like one
    // card copied rather than like a pile of arrived notices.
    public int ClockMinutes = 0;
    // Called when a preview card is clicked. A preview is owned by the settings
    // page, so a click does not close it: the host is told which slot was clicked,
    // puts the next kind there and redraws. A real card never sets this.
    public Action<int> OnPreviewDismiss = null;
    // Whether this preview card is the one drawn in full. It is not the same as
    // "slot 0", because a preview stack whose front rung was dropped still has a
    // front; the stack sets this on each card so the entrance can tell the front
    // from the rest.
    public bool DeckFront = false;
    // Which swap of the preview this card belongs to. A new stack is raised
    // before the old one is closed, so for a moment two stacks share the deck
    // directory; the tag keeps each stack looking only at its own rungs, and is
    // the reason a new card cannot stack itself behind an old one.
    public string PreviewStamp = "";
    // Drawing: which theme to paint in ("auto" follows Windows), whether to
    // animate, how much to scale the whole card, and which language decides the
    // face and the reading direction. All four are read once per card, like the
    // theme itself: a notice lives seconds.
    public string Theme = "auto";
    public string Animations = "follow";
    public string Scale = "auto";
    public string Language = "auto";
    // "auto" leaves the preset's own contrast; "high" and "soft" move the body
    // and footer one rank without touching the hue.
    public string Contrast = "auto";
    // The settings page raises a card of its own so the reader can see what the
    // controls do. It is the same card in every visual respect, and different in
    // every behavioural one: it never expires (the page decides how long it
    // lives), it ignores focus sessions, it keeps its own deck so it cannot push
    // a real notice out of the corner, and it never takes a hard deadline.
    public bool Preview = false;
    // A preview that replaces another one appears where it belongs instead of
    // sliding in: replaying the entrance on every keystroke in the settings page
    // would be a light show, not a preview.
    public bool Instant = false;
    // When this stack started arriving, as UTC ticks: 0 means "measure from this
    // card's own first frame". A preview stack sets one epoch for all its rungs,
    // because they are raised one after another and a card timing itself would
    // fade in later than the rung behind it, so the stack would appear from the
    // back forward instead of all at once.
    public long EntranceEpoch = 0;
    // When set, this process is the preview host: it owns the whole stack and
    // rebuilds it in place whenever this file changes, instead of the settings
    // page starting a fresh stack per edit. One process start costs ~300ms and
    // rebuilding a stack inside one costs ~40ms, which is the difference between
    // a preview that trails the slider and one that keeps up with it.
    public string PreviewSpec = "";

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
                case "--font-size": { int n; if (int.TryParse(v, out n)) o.FontSize = n; i++; break; }
                case "--theme": o.Theme = v; i++; break;
                case "--animations": o.Animations = v; i++; break;
                case "--scale": o.Scale = v; i++; break;
                case "--language": o.Language = v; i++; break;
                case "--contrast": o.Contrast = v; i++; break;
                case "--preview": o.Preview = true; break;
                case "--instant": o.Instant = true; break;
                case "--preview-slot": { int n; if (int.TryParse(v, out n)) o.PreviewSlot = n; i++; break; }
                case "--preview-stamp": o.PreviewStamp = v; i++; break;
                case "--preview-host": o.PreviewSpec = v; i++; break;
            }
        }
        if (o.Seconds < 1) o.Seconds = 1;
        return o;
    }

    // Only strings and value types live on Options, so a memberwise copy is a
    // real copy. The preview host re-reads the spec on every edit and needs a
    // fresh set to hand each card.
    public Options Clone()
    {
        return (Options)MemberwiseClone();
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
    // The type dial and everything measured from it are mutable: a font-size
    // change is applied to the card that is already on screen instead of raising
    // a new one, which is the difference between a slider that tracks the drag
    // and one that blinks a fresh window at every notch.
    float fontScale;
    Font captionFont;
    Font bodyFont;
    Font metaFont;
    // The small line above the headline (clock and session). Its own face, on the
    // grid size rather than on the type dial: see the constructor.
    readonly Font overlineFont;
    // The reader's type dial as this card currently has it, so a live restyle can
    // tell whether the glyphs actually have to be rebuilt.
    int fontSize;
    // The face the glyphs are drawn in, kept so a type change can rebuild the
    // three Font objects without picking a face again.
    string uiFont;
    // The face the running text is drawn in: the family above, or its drawn
    // Medium master where one is installed. See PickBodyFace.
    string bodyFace;
    // The headline's own face. A family that ships a heavier master (Noto Sans SC
    // has Medium as a separate family) gets it by name, which keeps the drawn
    // strokes; a family without one falls back to a synthetic bold of the same
    // family, which is the only option left.
    string heavyFace;
    FontStyle heavyStyle;

    // The card follows the Windows app theme. There is no API to subscribe to a
    // change, and a notice lives seconds, so the theme is read once when the card
    // is built: a card raised after the switch already has the new colours.
    bool dark;
    bool contrast;
    readonly bool animated;
    readonly bool touch;
    readonly bool rtl;
    readonly int wheelLines;
    // Not readonly any more: the preview host recolours a live card in place
    // rather than raising a new one, so that a colour change can be animated
    // across the card it already has on screen.
    Color surface;
    Color titleColor;
    Color bodyColor;
    Color mutedColor;
    Color hairlineColor;
    Color hoverColor;
    Color trackColor;
    Color thumbColor;

    // The palette a sweep is coming from. A colour change is drawn as a diagonal
    // wipe running from the top-left corner to the bottom-right one, so the old
    // palette has to sit next to the new one until the wipe is over.
    struct Palette
    {
        public Color Surface, Title, Body, Muted, Hairline, Hover, Track, Thumb;
    }
    Palette wipeFrom;
    Stopwatch wipeSw;
    bool wiping;
    // A panel that sits on top of the card for the length of a wipe. Painting the
    // sweep on the form itself would not work: the caption, body and footer are
    // child panels, and a child paints over its parent, so the old surface would
    // come back on top of the new one.
    BufferedPanel wipePanel;

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
    readonly int targetX, slotBase;
    // The strip a pile reveals is measured from the type the card is drawn with,
    // so both of these follow a live type change: see StripHeight and Reslot.
    int targetY;
    // Not readonly: the strip a pile reveals is measured from the type the card
    // is drawn with, and the type can change on a live card - see ReType.
    int slotOffset;
    // Which edge carries the clock-and-headline band. A bottom corner grows
    // upward and shows every card's top; a top corner mirrors that.
    readonly bool headerAtTop;
    readonly int bodyWidth, bodyHeight, barWidth;
    // These three follow the type, and the type can be changed on a live card, so
    // they are no longer readonly: see ReType.
    int lineHeight;
    List<string> bodyLines = new List<string>();
    int maxScroll, thumbHeight;
    readonly GraphicsPath shape;
    // Y of the hairline that separates the body from the permission row, or -1
    // when the card has no such row. Painted in OnPaint, because the footer row
    // itself is a transparent strip and cannot draw a line above itself.
    readonly int dividerY;

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
    // Milliseconds this card was late to its stack's entrance. See EntranceEpoch.
    double entranceSkewMs;

    bool answered;
    long answerAt;
    long lastSeen = -1;
    int ownerTick;
    Stopwatch clock;
    const long SeenGraceMs = 3000;

    // The top of the type dial (fontSize 160 on the settings page). Bands are sized
    // for this face rather than for the one the card is built with, so that one
    // geometry serves the whole dial and a live card can be re-lettered in place.
    const float MaxTypeScale = 1.6f;

    // How many lines of running text the body band holds. Counted in lines rather
    // than in grid units because that is the quantity a reader cares about: a fixed
    // 92 units held four lines at the default width and three once the type dial
    // went up. Six is enough for a real paragraph and still leaves a stack of three
    // inside a 900px screen. The band is this tall on every card, not just on the
    // long ones - a deck whose members are different heights stacks into a ragged
    // edge, and each card would compute a different "how many fit here" from its own
    // height.
    const int BodyLines = 6;

    // Line spacing as a multiple of the em. The font's own line box is 1.45em for
    // Noto Sans SC, which is looser than the Windows UI ramp (about 1.4) and makes
    // a short paragraph look airy and disconnected; this is the tighter leading a
    // system surface uses, applied in whole pixels so lines never drift off grid.
    const double BodyLeading = 1.40;

    // Samples per axis when the ink pass draws.
    //
    // The unevenness the reader reports - one stem solid, its neighbour grey - has
    // two sources, and both had to go. ClearType weights a pixel's three subpixels
    // differently, so the same stroke drawn one glyph later lands on a different
    // subpixel column and comes out at a different weight: grey coverage replaced
    // it, and that is the fix for stems disagreeing with each other. The other is
    // the grid itself: glyph advances are fractional, so each glyph starts at a
    // different phase. Measured by shifting the origin a third of a pixel, one
    // sample per axis changes the output by 37%; two samples box-averaged down is
    // true area coverage, so the same shift costs 20%.
    //
    // Three and four samples were measured and do not help: GDI+ grid-fits the
    // glyph before it rasterises, so a larger factor only buys a finer snap. The
    // price of two is weight, which CoverageGamma below pays back.
    const int InkScale = 2;

    // A card over a full-screen app must not take the mouse. See ApplyClickThrough.
    bool clickThrough;

    // The scrollbar's own opacity, and the clock that decides it. The bar is up
    // while the text is moving and gone a moment after it stops, so the right edge
    // of the body is quiet type again instead of a grey stripe that never leaves.
    // A timer of its own rather than the frame pump: the pump runs at 250Hz and
    // parks the moment nothing moves, which is right for a slide and wrong for a
    // bar that has to keep fading after the last wheel notch.
    const int ScrollFrameMs = 33;
    const double BarHoldMs = 400;
    const double BarFadeMs = 260;
    Timer scrollTimer;
    double barAlpha = 1.0;
    readonly Stopwatch barSw = Stopwatch.StartNew();

    // How many rungs back this card sits in the deck, 0 being the front one. Every
    // rung sinks the card's surface a little, so a stack reads as depth instead of
    // as the same card printed three times.
    int deckSlot;

    // The bottom-weighted edge, derived from the palette so it follows theme and
    // contrast instead of being a fixed grey. It is a gradient rather than two
    // pens: the border has to be lighter along the top and darker along the
    // bottom, and one gradient brush on one pen does that in a single DrawPath
    // instead of three arc bookkeepings.
    Color edgeTop;
    Color edgeBottom;

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
    //
    // An explicit language in the config wins over the system one, so a reader can
    // keep Chinese cards on an English Windows: the face has to follow the words
    // on the card, not the operating system underneath it.
    static string PickUiFont(string language)
    {
        string name = language == "zh" ? "zh" : language == "en" ? "en" : CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        string[] wanted;
        // Noto Sans SC first where it exists: its contours are drawn rounder than
        // YaHei's squared-off strokes, which is the whole point of listing it.
        // It is not on every Windows, and the loop simply moves on to the face
        // that is, so nothing breaks on a machine without it.
        if (name.StartsWith("zh")) wanted = new string[] { "Noto Sans SC", "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI Variable Text", "Segoe UI" };
        else if (name.StartsWith("ja")) wanted = new string[] { "Noto Sans JP", "Yu Gothic UI", "Meiryo UI", "Segoe UI Variable Text", "Segoe UI" };
        else if (name.StartsWith("ko")) wanted = new string[] { "Noto Sans KR", "Malgun Gothic", "Segoe UI Variable Text", "Segoe UI" };
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

    // The heavier master of the same family, for the headline. Font families on
    // Windows list each weight as its own family name rather than as a style of
    // one family, so a "Medium" here is a real drawn weight and not the synthetic
    // thickening FontStyle.Bold asks for. Null means the family has none and the
    // caller should bold the regular face instead.
    static string PickHeavyFace(string family)
    {
        // One notch above the body, never more. SemiBold where the family ships
        // it, Medium otherwise. Black was here first and is one step too far: at
        // 21px its stroke is 4.6px wide against the regular master's 2.4, which
        // reads as smeared rather than bold, and the width it adds over Medium is
        // exactly the halo that makes the headline look pasted onto the card.
        string[] wanted = new string[] {
            family + " SemiBold", family + " Semibold", family + " DemiBold",
            family + " Medium"
        };
        try
        {
            foreach (string candidate in wanted)
            {
                foreach (FontFamily installed in FontFamily.Families)
                    if (string.Equals(installed.Name, candidate, StringComparison.OrdinalIgnoreCase)) return installed.Name;
            }
        }
        catch { }
        return null;
    }

    // The body's own face: the family's regular master. A drawn Medium was tried
    // here to hold a cleaner edge, but at 18px it also widens every stem to 3.1px
    // from the regular master's 2.6 and the paragraph reads as emboldened, which
    // is what made the card look heavy against its own background.
    static string PickBodyFace(string family)
    {
        return family;
    }

    // Right-to-left reading order, from the UI language. Windows itself mirrors
    // its own chrome for these cultures, and so does this card: control buttons
    // swap sides, and the "right" half of a footer line is the one that ends the
    // sentence. A card pinned to English or Chinese is never mirrored.
    static bool IsRightToLeft(string language)
    {
        if (language == "zh" || language == "en") return false;
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

    // The scale is a percentage of the designed 420-unit card: the settings page
    // hands over a slider, so any whole percent in range has to be readable here,
    // not just the four presets this used to accept. "auto" - or anything that is
    // not a number - means "measure the screen instead".
    //
    // Two spellings are accepted. A percentage ("125") is what the slider writes.
    // A bare factor ("1.25") is what the presets wrote before the slider existed,
    // and a config saved then still has to open at the same size; below ten, a
    // number cannot be a percentage, because no card is under a tenth of itself.
    static bool TryParseScale(string value, out float scale)
    {
        scale = 0f;
        if (string.IsNullOrEmpty(value)) return false;
        string v = value.Trim();
        if (v == "auto") return false;
        double n;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return false;
        if (n < 10) n *= 100.0;
        if (n < 60) n = 60;
        if (n > 200) n = 200;
        scale = (float)(n / 100.0);
        return true;
    }

    // Contrast is a dial, not a switch: +1 pushes the body towards the title and
    // the border towards full strength, -1 lets both fall back towards the
    // surface, 0 is the preset's own ranking. The settings page shows it as a
    // slider from 0 to 100 with 50 in the middle, so the value here is that
    // number rescaled to -1..1.
    //
    // The three names this used to accept are still read, so a config written
    // before the dial existed keeps the look it was saved with.
    static double ContrastRank(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0.0;
        string v = value.Trim();
        if (v == "high") return 1.0;
        if (v == "soft") return -1.0;
        if (v == "auto") return 0.0;
        double n;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0.0;
        if (n < 0) n = 0;
        if (n > 100) n = 100;
        return (n - 50.0) / 50.0;
    }

    // One rank lighter (amount > 0) or darker, as a mix towards white or black.
    // Shifting in that space keeps the hue and only moves the value, which is
    // what "a step down in brightness" means for a footer line.
    static Color Shift(Color c, double amount)
    {
        return Mix(c, amount >= 0 ? Color.White : Color.Black, Math.Abs(amount));
    }

    static Color Mix(Color a, Color b, double t)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
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

        dark = opt.Theme == "dark" || (opt.Theme != "light" && Native.AppsUseDarkTheme());
        contrast = Native.HighContrast();
        // Animations off in Windows, or a machine on battery saver, both mean the
        // same thing for this card: skip the motion, keep the pixels. An explicit
        // "on" in the config outranks both, "off" outranks the system.
        animated = opt.Animations == "on" || (opt.Animations != "off" && Native.AnimationsEnabled() && !Native.DrawingLess());
        touch = Native.TouchPresent();
        wheelLines = Native.WheelLines();
        // Reading direction comes from the UI language. Arabic, Hebrew, Persian and
        // Urdu write right to left, and every Windows shell mirrors its layout for
        // them; a card that did not would be the one window facing the other way.
        rtl = IsRightToLeft(opt.Language);

        // The palette is built in three passes, in this order: the theme preset,
        // then whatever the config pins, then the contrast tier. High contrast
        // comes last and replaces the lot below, because it is the user telling
        // Windows that no palette it did not choose is readable.
        BuildPalette();

        string ui = PickUiFont(opt.Language);
        uiFont = ui;
        // The headline's face: the same family in a heavier master where one is
        // installed, otherwise the family itself plus a synthetic bold.
        heavyFace = ui;
        heavyStyle = FontStyle.Bold;
        string medium = PickHeavyFace(ui);
        if (medium != null) { heavyFace = medium; heavyStyle = FontStyle.Regular; }
        // Running text steps up to the drawn Medium of the same family where it is
        // installed: the regular master is thin enough at body size that the
        // anti-aliased stems come out half-covered and the line reads as uneven.
        string bodyFacePick = PickBodyFace(ui);
        bodyFace = bodyFacePick;

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
        int deviceWidth = (int)Math.Round(area.Width * 0.175 * textScale);
        deviceWidth = Math.Max((int)Math.Round(290 * scale * textScale), Math.Min((int)Math.Round(490 * scale * textScale), deviceWidth));
        // A pinned scale multiplies the automatic width, not the 420-unit design
        // grid. That makes 100 mean "the size this screen would pick on its own",
        // so opening the settings page and touching the dial without moving it
        // does not resize the card. It used to multiply the grid, which put 100 at
        // a different size than auto on every screen whose width is not a whole
        // multiple of 420 units, and the first drag jumped.
        float pinnedScale;
        if (TryParseScale(opt.Scale, out pinnedScale))
            deviceWidth = Math.Max(1, (int)Math.Round(deviceWidth * pinnedScale));
        // The layout grid stays a true ratio - it has to, since the design is 420
        // units wide and the window is `deviceWidth` pixels - but every coordinate
        // derived from it is snapped to a whole device pixel, and the two font
        // sizes below are whole pixels as well. Those are the parts that decide
        // whether glyphs sit on the grid or between its pixels.
        unit = deviceWidth / 420f;
        // Type size is its own dial on top of the grid, and it moves the glyphs
        // only. The bands keep the grid's heights, so the card is the size the
        // screen asked for no matter what the reader picked here; larger type
        // shows fewer lines in the same body box, which the body already scrolls.
        fontScale = Math.Max(0.5f, Math.Min(2.5f, opt.FontSize / 100f));
        fontSize = opt.FontSize;
        // Integer pixel sizes only. GDI rasterizes a hinting-friendly em size;
        // 18.29px asks for an em no font was hinted for and the stems come out at
        // inconsistent widths from one glyph to the next.
        int textPx = Math.Max(15, (int)Math.Round(15 * unit * fontScale, MidpointRounding.AwayFromZero));

        // Whole device pixels, not points: 11pt at 120dpi is 18.33px and that
        // fractional em size lands glyph stems off the pixel grid.
        //
        // The headline takes a heavier face of the same family rather than a
        // synthetic bold: Noto ships Medium as its own family, so the weight is a
        // drawn master with the same shapes as the body instead of thickened
        // pixels. Where the family has no medium master, the picker returns null
        // and the title falls back to FontStyle.Bold.
        int titlePx = Math.Max(18, (int)Math.Round(20 * unit * fontScale, MidpointRounding.AwayFromZero));
        captionFont = new Font(heavyFace, titlePx, heavyStyle, GraphicsUnit.Pixel);
        bodyFont = new Font(bodyFace, textPx, FontStyle.Regular, GraphicsUnit.Pixel);
        // The footer is the smallest type on the card, which is exactly where a
        // fractional em hurts most: at 13px a half-pixel of stem drift is a whole
        // stroke. It stays on a whole pixel too, and never below 12px.
        int metaPx = Math.Max(12, (int)Math.Round(12 * unit * fontScale, MidpointRounding.AwayFromZero));
        metaFont = new Font(ui, metaPx, FontStyle.Regular, GraphicsUnit.Pixel);
        // The header overline is deliberately NOT on the type dial. It sits in the
        // same strip as the headline, and a dial that grew both would push them
        // into each other at the top of the range; keeping the overline at the
        // grid size means the headline can still grow into the strip.
        //
        // It is drawn from the Medium master, not the regular one, and a size up
        // from the old 11 units, because it is the smallest type on the card and
        // the first thing a stacked card shows. Measured at 12px on a dark card:
        // the regular master leaves 75% of its stems one pixel wide and only 21%
        // of its ink fully covered, which is the uneven clock the reader kept
        // reporting. Medium puts 31% fully covered and 47% of stems on two
        // pixels, and 13px moves another 6% of stems off the single-pixel case.
        int overlinePx = Math.Max(13, (int)Math.Round(12 * unit, MidpointRounding.AwayFromZero));
        overlineFont = new Font(bodyFace, overlinePx, FontStyle.Regular, GraphicsUnit.Pixel);

        // The clock, the session name and the headline are the three things a
        // card in a pile has to say without being pulled out of the pile, so they
        // travel together in one band and that band always sits on the edge the
        // pile reveals. A bottom-anchored deck grows upward and shows every card's
        // top; a top-anchored one grows downward and shows its bottom. Putting the
        // band on the wrong edge is what made a stacked card show only the clock
        // and half a headline.
        DateTime stamp = DateTime.Now.AddMinutes(opt.ClockMinutes);
        string clockText = stamp.ToString("t", CultureInfo.CurrentCulture);
        string metaLine = opt.Session.Length != 0 ? clockText + " · " + opt.Session : clockText;
        headerAtTop = !topCorner;
        bool named = opt.Preset.Length != 0;
        // The permission mode is a corner mark, not a caption: it belongs on the
        // far edge of its own row, where the eye reads it as the card ending
        // rather than as a label that got left behind - that stray label was the
        // ugliest part of the old layout. It takes the edge the header does not.
        //
        // The band has to fit inside the strip a stacked card shows (slotOffset):
        // three units of air, a line for the clock and the session, then the
        // headline in what is left. The air above the clock is what the first
        // version of this missed - two units put the line right on the card's top
        // edge and jammed it against the headline.
        int metaHeaderTop = Px(3);
        // Every band is measured from the largest face the type dial can ask for,
        // not from the one this card happens to be built with. Two things depend on
        // that: a band sized for the current face clips the moment the reader drags
        // past it (at 160 the headline has 45px of line box and a 24px band has
        // nowhere to put it), and a geometry that moved with the type could not be
        // re-lettered in place, which is what keeps the card from blinking on every
        // notch (see SameGeometry and ReType). Sized at the top of the dial, one
        // geometry serves the whole range and no glyph ever runs out of its box.
        float bandScale = Math.Max(MaxTypeScale, fontScale);
        Font bandCaption = new Font(heavyFace, Math.Max(18, (int)Math.Round(20 * unit * bandScale, MidpointRounding.AwayFromZero)), heavyStyle, GraphicsUnit.Pixel);
        Font bandMeta = new Font(ui, Math.Max(12, (int)Math.Round(12 * unit * bandScale, MidpointRounding.AwayFromZero)), FontStyle.Regular, GraphicsUnit.Pixel);
        int captionHigh = Math.Max(Px(20), LineHigh(bandCaption));
        int footerRowHigh = Math.Max(Px(20), LineHigh(bandMeta));
        // The body band is counted in lines, not in grid units. A fixed 92 units
        // held four lines at the default width and three once the type dial went up,
        // which is why the card kept looking cramped. Six lines, measured at the
        // reference face rather than at the reader's type size, so one geometry
        // still serves the whole dial and a card can be re-lettered in place.
        Font bandBody = new Font(bodyFace, Math.Max(15, (int)Math.Round(15 * unit, MidpointRounding.AwayFromZero)), FontStyle.Regular, GraphicsUnit.Pixel);
        int bodyWidthPx = Px(372);
        int bodyLineHigh = BodyLinePx(bandBody);
        bandCaption.Dispose();
        bandMeta.Dispose();
        bandBody.Dispose();
        // The band that a pile reveals, assembled from measured pieces: one line
        // for the clock and the session, two units of air, then the headline.
        int metaRowHigh = Math.Max(Px(20), LineHigh(overlineFont));
        int bodyHigh = BodyLines * bodyLineHigh;
        // Both layouts are the same four blocks in a different order, and both are
        // flush to the edge they are anchored to. Nothing here is a fixed number
        // from the 420-unit grid except the air between blocks, so a wider card or
        // a larger type size moves the whole stack instead of breaking it.
        int metaY, captionY, bodyY, permY;
        if (headerAtTop)
        {
            metaY = metaHeaderTop;
            captionY = metaY + metaRowHigh + Px(2);
            bodyY = captionY + captionHigh + Px(2);
            permY = bodyY + bodyHigh + Px(10);
        }
        else
        {
            permY = Px(8);
            bodyY = permY + (named ? footerRowHigh : 0) + Px(6);
            metaY = bodyY + bodyHigh + Px(10);
            captionY = metaY + metaRowHigh + Px(2);
        }
        int logicalHeight = headerAtTop
            ? permY + (named ? footerRowHigh : 0) + Px(8)
            : captionY + captionHigh + Px(8);

        AutoScaleMode = AutoScaleMode.None;
        Text = opt.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = surface;
        Size = new Size(deviceWidth, logicalHeight);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        // Both edges are flush: a bottom corner sits flat on the taskbar, a top
        // corner is level with the top of the screen. `area` is the working area,
        // so the taskbar is already excluded from it.
        targetX = leftCorner ? area.Left : area.Right - Width;
        // Cards overlap instead of tiling: every card except the front one keeps
        // only this strip visible past the card in front of it. The deck runs away
        // from the anchored edge, so a top corner stacks downward and a bottom one
        // upward.
        //
        // The strip has to be at least as tall as the ink it is meant to show -
        // the clock, the session name and the headline - or the stack hides the
        // very thing it shows it for. It is measured from the faces the card is
        // drawn with, not from the bands: a band is sized for the largest type the
        // dial can ask for, and charging the slack of that band to every card in a
        // pile is what left a gap of empty surface between one card's headline and
        // the next card's clock. The headline hangs from the revealed edge (see
        // TextBoxHang), so the slack falls behind the card in front instead.
        slotOffset = StripHeight();
        slotBase = topCorner ? area.Top : area.Bottom - Height;

        // The preview deck lives in its own directory. It is replaced on every
        // change in the settings page, and it must never count towards - or push
        // a real notice out of - the corner it is being used to show off.
        cardDir = opt.Preview
            ? Path.Combine(Path.GetTempPath(), "dsh-notice-cards", "preview")
            : Path.Combine(Path.GetTempPath(), "dsh-notice-cards");
        try { Directory.CreateDirectory(cardDir); } catch { }
        // The corner is part of the file name, not just the window position: each
        // corner is its own deck. Sharing one would let a bottom-left card count
        // the cards in a top-right stack and land in a slot no one can see.
        //
        // A preview also writes its rung into the name. The pages raises every
        // rung of the stack at once, so the processes overlap and their clock
        // order says nothing; the rung has to come from the argument, and the
        // name is where the next card looks it up.
        // A preview also writes its rung and its stack's tag into the name. The
        // page raises every rung of a stack at once, so the processes overlap and
        // their clock order says nothing; the rung has to come from the argument,
        // and the name is where the next card looks it up. The tag separates two
        // stacks for the moment they coexist during a swap: a new card must look
        // up its own siblings, never the ones that are about to be closed.
        cardFile = Path.Combine(cardDir, opt.Preview
            ? string.Format("{0:D19}-{1}-{2}p{3}s{4}.card", DateTime.Now.Ticks, Process.GetCurrentProcess().Id, cornerKey, opt.PreviewSlot, opt.PreviewStamp)
            : string.Format("{0:D19}-{1}-{2}.card", DateTime.Now.Ticks, Process.GetCurrentProcess().Id, cornerKey));

        targetY = SlotY();
        Location = new Point(targetX, targetY + (topCorner ? -Px(28) : Px(28)));
        Opacity = 0;
        SinkForDeck();

        // Rounded corners: a Region clips everything drawn, including the border,
        // so the shape stays consistent at every size. 8px is the toast radius.
        shape = Round(new Rectangle(0, 0, Width, Height), Px(8));
        Region = new Region(shape);

        // Both text blocks are hand-painted rather than living in a Label, because
        // a Label brings its own padding, its own font inheritance and its own
        // redraw policy. What is painted is GDI ClearType (see PaintBody), so the
        // wrapping is measured with the same engine that draws it.
        bodyWidth = bodyWidthPx;
        bodyHeight = bodyHigh;
        barWidth = Px(8);
        // One whole pixel per line, from GDI's own line spacing. A fractional line
        // height accumulated over ten lines is a whole pixel of drift by the
        // bottom of the card, and every line lands a little further off the grid.
        MeasureBody(opt.Body);

        // The title sits in its own panel so a click anywhere on it still dismisses.
        // GDI text again (see PaintBody): the title is the most looked-at line on
        // the card, so it gets the same ClearType treatment as the body rather
        // than a fat greyscale smear.
        BufferedPanel caption = new BufferedPanel();
        caption.Location = new Point(Px(20), captionY);
        // The headline owns its whole row now that the corner buttons moved up to
        // the clock: the two ends of the grid are both free, so the longest session
        // name a card ever shows gets the full 380 units before it is trimmed.
        caption.Size = new Size(Px(380), captionHigh);
        caption.BackColor = surface;
        caption.Paint += delegate(object s, PaintEventArgs e)
        {
            // Trimmed with an ellipsis instead of cut mid-glyph: the panel width is
            // fixed and the headline is whatever the session is called, so at the
            // top of the type dial a long title always runs past it.
            RectangleF box = new RectangleF(0, 0, caption.Width, caption.Height);
            InkText(e.Graphics, opt.Title, captionFont, titleColor, box, headerAtTop ? TextBoxHang : TextBoxHangUp);
        };
        caption.Click += delegate { Dismiss(); };
        Controls.Add(caption);

        // The two corner buttons ride the clock line, not the headline: they are
        // chrome, and the line they belong to is the one that carries the time.
        // The button is the height of that band and centred in it, so the glyphs
        // and the timestamp sit on one row at every type size - a button sized for
        // a fingertip instead would be taller than the band and hang off it.
        //
        // A right-to-left interface puts its window controls on the left, so the
        // pair swaps sides there. The glyphs themselves are direction-free.
        int iconSize = Math.Max(16, (int)Math.Round(metaRowHigh / (double)unit, MidpointRounding.AwayFromZero));
        int iconGap = 4;
        int iconMargin = 4;
        int iconRightLeft = rtl ? iconMargin : 420 - iconMargin - iconSize;
        int iconLeftLeft = rtl ? iconMargin + iconSize + iconGap : iconRightLeft - iconGap - iconSize;
        // AddIconButton takes grid units, so the pixel row is divided back.
        int iconTop = (int)Math.Round((metaY + (metaRowHigh - Px(iconSize)) / 2.0) / unit, MidpointRounding.AwayFromZero);
        // The clock and the session name stop short of the buttons, which share
        // this row; a long name gives up its tail rather than pushing them off it.
        // A right-to-left interface mirrors the strip with the rest of the layout
        // instead of pinning one end to the same screen side.
        int metaLeft = rtl ? iconLeftLeft + iconSize + iconGap : 20;
        int metaRight = rtl ? 420 - 20 : iconLeftLeft - iconGap;
        AddFooterRow(metaY, metaLeft, metaRight - metaLeft, metaLine, rtl, overlineFont);
        AddIconButton(iconLeftLeft, iconTop, iconSize, DrawWindowGlyph, delegate { if (!opt.Preview) FocusApp(); Dismiss(); });
        AddIconButton(iconRightLeft, iconTop, iconSize, DrawCloseGlyph, delegate { Dismiss(); });

        // The body scrolls instead of running off the bottom. WinForms controls
        // cannot be scrolled without keyboard focus and this card deliberately
        // never takes focus, so the offset is ours: the wheel adjusts it, and the
        // thumb can be dragged, which works because plain mouse messages do reach
        // the card.
        bodyBox = new BufferedPanel();
        bodyBox.Location = new Point(Px(20), bodyY);
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
            BarAwake();
            // The pump stops itself once the entrance animation and any glide are
            // done, so a later wheel has to bring it back for the easing frames.
            if (settled) StartPump(4);
        };
        bodyBox.MouseDown += delegate(object s, MouseEventArgs e)
        {
            if (e.X >= bodyWidth && maxScroll > 0)
            {
                dragging = true;
                BarAwake();
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
                BarAwake();
                double travel = Math.Max(1, bodyHeight - thumbHeight);
                scroll = Math.Max(0.0, Math.Min((double)maxScroll, (e.Y - dragGrab) * maxScroll / travel));
                scrollTarget = scroll;
                bodyBox.Invalidate();
            }
        };
        Controls.Add(bodyBox);

        // The permission line is a read-only echo of the client's dropdown: the
        // name only. Switching it from here would change the sandbox under a
        // running tool call, and that aborts the call. It sits on the far edge of
        // its row, so the row reads as the card closing rather than as a caption
        // that lost its place, and it takes the edge the header band does not: the
        // footer under a bottom corner, the top of a top corner.
        if (named)
        {
            AddFooterRow(permY, 20, 380, opt.Preset, headerAtTop ? !rtl : rtl, null);
            dividerY = headerAtTop ? permY - Px(2) : permY + footerRowHigh + Px(2);
        }
        else
        {
            dividerY = -1;
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
        //
        // A preview has no dismiss file and no life timer, so this watchdog is
        // its only way out when the page that raised it goes away: the owner
        // process check below closes it with DSH.
        if (opt.DismissFile.Length != 0 || opt.Preview)
        {
            clock = Stopwatch.StartNew();
            watch = new Timer();
            // The tick is also the preview's replacement detector, so it decides
            // how long a stale card outlives its stack. A tenth of a second is a
            // visible leftover on every keystroke; six hundredths is shorter than
            // the debounce that raises the next stack, so a swap reads as one card
            // changing rather than as two cards, one of them wrong.
            watch.Interval = opt.Preview ? 60 : 100;
            watch.Tick += delegate { WatchTick(); };
        }

        // The scrollbar's fade. A card whose text fits never needs this timer at
        // all, and OnShown only starts it when there was something to scroll.
        scrollTimer = new Timer();
        scrollTimer.Interval = ScrollFrameMs;
        scrollTimer.Tick += delegate { ScrollTick(); };

        // The wipe sheet. Created hidden and empty; Restyle carves it down to the
        // part of the card that still shows the previous palette.
        wipePanel = new BufferedPanel();
        wipePanel.Bounds = new Rectangle(0, 0, Width, Height);
        wipePanel.BackColor = surface;
        wipePanel.Visible = false;
        wipePanel.Paint += delegate(object sender, PaintEventArgs e)
        {
            if (!wiping) return;
            using (SolidBrush brush = new SolidBrush(wipeFrom.Surface))
                e.Graphics.FillRectangle(brush, wipePanel.ClientRectangle);
        };
        Controls.Add(wipePanel);

        frame = delegate
        {
            if (exiting) { ExitFrame(); return; }
            if (wiping) WipeFrame();
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
    //
    // The border is a vertical gradient rather than one flat grey: it sits almost
    // at the surface along the top and carries the full hairline along the bottom,
    // so the card reads as a sheet with light falling on it. The Region clips the
    // window to the rounded rect, which is why a real shadow cannot be painted
    // here, and a darker bottom edge is the closest an unlayered form gets.
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // The ring is filled, not stroked with a gradient pen: GDI+ only honours a
        // gradient brush along part of a stroked path, which is why stroking left
        // one edge painted and the other three bare. Filling the outer rounded rect
        // and then punching the surface back out gives all four sides the same ramp.
        using (LinearGradientBrush edge = new LinearGradientBrush(
            new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), edgeTop, edgeBottom,
            LinearGradientMode.Vertical))
            e.Graphics.FillPath(edge, shape);
        int rimInset = Math.Max(1, Px(1));
        int rimRadius = Math.Max(1, Px(8) - rimInset);
        using (GraphicsPath inner = Round(
            new Rectangle(rimInset, rimInset, Math.Max(1, Width - rimInset * 2), Math.Max(1, Height - rimInset * 2)),
            rimRadius))
        using (SolidBrush fill = new SolidBrush(surface))
            e.Graphics.FillPath(fill, inner);
        // A hairline above the permission row. Without it the row reads as a
        // label that fell off the card; with it, it reads as the card's own
        // footer, which is what it is. -1 means the card has no such row.
        if (dividerY > 0)
        {
            using (Pen pen = new Pen(hairlineColor))
                e.Graphics.DrawLine(pen, Pf(20), dividerY, Width - Pf(20), dividerY);
        }
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

    // --- palette ---

    // The palette is monochrome on purpose. Two versions exist - black on white
    // and white on black - and `theme` picks which one, either by name or by
    // following Windows. There is no user colour any more: the earlier build let
    // the config pin a surface and an ink, and every derived colour (border,
    // hover wash, scrollbar) then had to be guessed from it, which is how the
    // card ended up with blue-grey tints that read as dirt next to real ink.
    //
    // Written as a free function returning a struct rather than as assignments
    // into the fields, because Restyle needs to compute a candidate palette
    // without touching the one on screen - it has to know whether anything
    // changed before deciding to animate a change.
    static Palette PaletteOf(Options o, bool dark, bool contrast)
    {
        Palette p;
        // The two ends come from the theme itself, not from a pair written here:
        // "the card's black and white are the system's black and white". On a
        // stock dark theme that is exactly black on white inverted, which is what
        // this used to hard-code, but a themed shell can answer something else and
        // then the card belongs to that desktop instead of merely matching its
        // lightness.
        Color paper, ink;
        Native.ShellInk(dark, out paper, out ink);
        p.Surface = paper;
        bool deep = dark;
        p.Title = ink;
        p.Body = Mix(paper, ink, deep ? 0.90 : 0.86);
        // The two text greys are the Settings app's own, not a mix of the ends:
        // on a dark surface Settings writes #FFFFFF and #CFCFCF, and a card whose
        // footer is any dimmer than that grey reads as disabled text. Light mode
        // mirrors it with #1A1A1A and #5F5F5F.
        p.Muted = deep ? Color.FromArgb(0xCF, 0xCF, 0xCF) : Color.FromArgb(0x5F, 0x5F, 0x5F);
        // Edges, hover wash and scrollbar are white-on-dark and black-on-light
        // washes: transparent ink, so they tint whatever surface ships without
        // ever introducing a hue of their own.
        // Settings separates a card from the page with a line a shade *darker*
        // than the card, never with a light rim: on #2B2B2B the separator is
        // around #1E1E1E, on #FFFFFF around #E5E5E5. A white rim on a dark card
        // is the single most obvious tell that this is not the shell's own card.
        p.Hairline = Mix(p.Surface, deep ? Color.Black : Color.FromArgb(0x80, 0x80, 0x80), deep ? 0.30 : 0.10);
        p.Hover = deep ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0x00, 0x00, 0x00);
        // The scrollbar follows Settings too: a dark card grows a light thumb on
        // a darker trough, a light card the other way round. Tinting both from the
        // surface used to leave the thumb the same value as its track on a dark
        // card, i.e. no scrollbar at all.
        p.Track = deep ? Mix(p.Surface, Color.Black, 0.30) : Mix(p.Surface, Color.Black, 0.07);
        p.Thumb = deep ? Mix(p.Surface, Color.White, 0.35) : Mix(p.Surface, Color.Black, 0.30);

        // Contrast is a dial, not three names: the rank runs -1..1 and every rank
        // in between has to be a card, so the body, the footer and the border all
        // move continuously. The two ends of the range land exactly on the mixes
        // this used to hard-code for "high" and "soft".
        double rank = ContrastRank(o.Contrast);
        if (rank > 0.0)
        {
            p.Body = Mix(p.Body, p.Title, 0.75 * rank);
            p.Muted = Mix(p.Muted, p.Title, 0.45 * rank);
        }
        else if (rank < 0.0)
        {
            double back = -rank;
            p.Body = Shift(p.Body, (deep ? -0.16 : 0.16) * back);
            p.Muted = Shift(p.Muted, (deep ? -0.16 : 0.16) * back);
        }
        // The border has its own endpoints, and it is the part that most needs the
        // dial: at the top the separator is drawn at nearly twice the preset's
        // strength so the edge reads at a glance, at the bottom at a little over
        // half so a soft card still reads as a card rather than as a hole in the
        // desktop. It stays a *darkening* of the surface at every rank, because
        // that is the only kind of edge Settings ever draws.
        {
            double baseEdge = deep ? 0.30 : 0.10;
            double highEdge = deep ? 0.55 : 0.22;
            double softEdge = deep ? 0.18 : 0.06;
            double edge = rank >= 0
                ? baseEdge + (highEdge - baseEdge) * rank
                : baseEdge + (softEdge - baseEdge) * -rank;
            if (edge < 0) edge = 0;
            if (edge > 1) edge = 1;
            p.Hairline = Mix(p.Surface, Color.Black, edge);
        }

        // High contrast outranks the theme: it is the user telling Windows that no
        // palette it did not choose is readable. The card then borrows the system
        // colours wholesale, so it matches whatever scheme is loaded instead of
        // guessing at a variant of the one above.
        if (contrast)
        {
            p.Surface = SystemColors.Window;
            p.Title = SystemColors.WindowText;
            p.Body = SystemColors.WindowText;
            p.Muted = SystemColors.GrayText;
            p.Hairline = SystemColors.WindowText;
            p.Hover = SystemColors.Control;
            p.Track = SystemColors.ControlDark;
            p.Thumb = SystemColors.ControlDarkDark;
        }
        return p;
    }

    void BuildPalette()
    {
        Palette p = SinkPalette(PaletteOf(opt, dark, contrast));
        surface = p.Surface;
        titleColor = p.Title;
        bodyColor = p.Body;
        mutedColor = p.Muted;
        hairlineColor = p.Hairline;
        hoverColor = p.Hover;
        trackColor = p.Track;
        thumbColor = p.Thumb;
        EdgeColors(p, contrast, out edgeTop, out edgeBottom);
    }

    // A stack of three identical cards at three identical greys does not read as a
    // stack, it reads as one card with a printing fault on its edge. Each rung back
    // sinks the surface a little, so the rungs in between look like they are in
    // shadow. Only the surface moves: the type keeps the contrast the palette gave
    // it, which is what makes a partly hidden card still legible on its strip.
    //
    // Three rungs is as far as this goes. Beyond that the back of a deck is a
    // hairline high, and sinking it further would make it a black stripe.
    Palette SinkPalette(Palette p)
    {
        if (deckSlot <= 0) return p;
        int rung = Math.Min(deckSlot, 3);
        double amount = (dark ? 0.085 : 0.040) * rung;
        p.Surface = Mix(p.Surface, Color.Black, amount);
        p.Hairline = Mix(p.Hairline, Color.Black, amount * 0.6);
        p.Track = Mix(p.Track, Color.Black, amount * 0.5);
        p.Hover = Mix(p.Hover, Color.Black, amount * 0.5);
        return p;
    }

    // The two colours the chrome needs on top of the eight the palette defines.
    // Kept in one place so BuildPalette and Restyle derive them the same way from
    // one palette instead of each carrying its own copy of the arithmetic.
    //
    // The edge is directional: almost the surface along the top, the full hairline
    // along the bottom. A border that is one grey all the way round reads as a
    // rectangle punched out of the desktop; one that is a shade heavier at the
    // bottom reads as a sheet lying on it, which is the job the drop shadow this
    // window cannot draw would otherwise do.
    static void EdgeColors(Palette p, bool contrast, out Color top, out Color bottom)
    {
        if (contrast)
        {
            // No fade in high contrast. That mode exists because the user asked
            // for an edge they can actually see, so it stays one colour.
            top = p.Hairline;
            bottom = p.Hairline;
            return;
        }
        top = Mix(p.Surface, p.Hairline, 0.55);
        bottom = Mix(p.Hairline, Color.Black, 0.30);
    }

    // The palette is built before the window, but which rung of the deck this card
    // ends up on is only known once SlotY has counted the directory - which needs
    // the window's height, so it cannot happen sooner. This runs the sink pass once
    // more when the answer arrives, and repaints the chrome if it moved anything.
    void SinkForDeck()
    {
        if (deckSlot <= 0) return;
        Palette before = CurrentPalette();
        Palette after = SinkPalette(before);
        if (SamePalette(before, after)) return;
        surface = after.Surface;
        hairlineColor = after.Hairline;
        trackColor = after.Track;
        hoverColor = after.Hover;
        EdgeColors(after, contrast, out edgeTop, out edgeBottom);
        PaintChrome();
    }

    Palette CurrentPalette()
    {
        Palette p;
        p.Surface = surface;
        p.Title = titleColor;
        p.Body = bodyColor;
        p.Muted = mutedColor;
        p.Hairline = hairlineColor;
        p.Hover = hoverColor;
        p.Track = trackColor;
        p.Thumb = thumbColor;
        return p;
    }

    static bool SamePalette(Palette a, Palette b)
    {
        return a.Surface.ToArgb() == b.Surface.ToArgb()
            && a.Title.ToArgb() == b.Title.ToArgb()
            && a.Body.ToArgb() == b.Body.ToArgb()
            && a.Muted.ToArgb() == b.Muted.ToArgb()
            && a.Hairline.ToArgb() == b.Hairline.ToArgb()
            && a.Hover.ToArgb() == b.Hover.ToArgb()
            && a.Track.ToArgb() == b.Track.ToArgb()
            && a.Thumb.ToArgb() == b.Thumb.ToArgb();
    }

    // Everything a palette change does not move. The preview host compares these
    // to decide whether it can recolour the card it already has or has to raise a
    // new one: colours can be swept across a live card, geometry cannot.
    internal bool SameLayout(Options next)
    {
        return SameGeometry(next)
            && string.Equals(opt.Title, next.Title, StringComparison.Ordinal)
            && string.Equals(opt.Body, next.Body, StringComparison.Ordinal);
    }

    // The half of the layout a card cannot be talked out of: width, height, edge.
    // A change here rebuilds the whole stack, because every card is measured
    // against it. Type size is not in here: it moves the glyphs inside the same
    // box, so a live card can be re-lettered in place (see ReType).
    internal bool SameGeometry(Options next)
    {
        return string.Equals(opt.Corner, next.Corner, StringComparison.Ordinal)
            && string.Equals(opt.Scale, next.Scale, StringComparison.Ordinal)
            && string.Equals(opt.Language, next.Language, StringComparison.Ordinal);
    }

    // Recolour in place. The card keeps its window, its position and its text, and
    // the swap is drawn as a diagonal wipe from the top-left corner to the
    // bottom-right one, so a colour dragged in the settings page reads as the card
    // changing rather than as one card being replaced by another.
    internal void Restyle(Options next)
    {
        bool nextDark = next.Theme == "dark" || (next.Theme != "light" && Native.AppsUseDarkTheme());
        bool nextContrast = Native.HighContrast();
        Palette wanted = SinkPalette(PaletteOf(next, nextDark, nextContrast));
        Palette current = CurrentPalette();
        if (nextDark == dark && nextContrast == contrast && SamePalette(wanted, current)) return;

        // The wipe is for a change of paper: it hides the card under the old
        // surface and sweeps it off, which only reads right when the surface
        // itself is what moved. The contrast dial moves ink and edge only, and it
        // moves them while the reader is dragging, so wiping there would cover the
        // text on every step of a drag - the reader would be watching a card that
        // keeps going blank instead of watching the text darken. Ink changes are
        // repainted straight through.
        bool surfaceMoved = nextDark != dark || wanted.Surface.ToArgb() != current.Surface.ToArgb();

        dark = nextDark;
        contrast = nextContrast;
        surface = wanted.Surface;
        titleColor = wanted.Title;
        bodyColor = wanted.Body;
        mutedColor = wanted.Muted;
        hairlineColor = wanted.Hairline;
        hoverColor = wanted.Hover;
        trackColor = wanted.Track;
        thumbColor = wanted.Thumb;
        EdgeColors(wanted, nextContrast, out edgeTop, out edgeBottom);
        PaintChrome();

        if (!surfaceMoved) return;

        // Not gated on the Windows animation switch, unlike the entrance and the
        // exit. Those move the window; this one only cross-fades a colour on a
        // card the reader is already looking at, and it exists because a colour
        // slider with no feedback is unreadable - the reader cannot tell which
        // part of the card the setting touched.
        wipeFrom = current;
        wipeSw = Stopwatch.StartNew();
        wiping = true;
        if (wipePanel != null)
        {
            wipePanel.Bounds = new Rectangle(0, 0, Width, Height);
            wipePanel.Visible = true;
            wipePanel.BringToFront();
        }
        StartPump(4);
    }

    // Push the palette into every child that paints it, so the wipe has a card to
    // reveal rather than a card with new text on an old surface.
    void PaintChrome()
    {
        BackColor = surface;
        for (int i = 0; i < Controls.Count; i++)
        {
            Control c = Controls[i];
            if (c == wipePanel) continue;
            if (!(c is BufferedPanel)) continue;
            c.BackColor = surface;
            c.Invalidate();
        }
        Invalidate(true);
    }

    // The wipe covers the card with the old surface and takes it away along a
    // line from the top-left corner to the bottom-right one, uncovering what the
    // new palette already painted underneath.
    //
    // The polygon is applied as the panel's Region rather than as a clip inside a
    // full-size panel: the card's caption, body and footer are siblings of this
    // panel, and a sibling cannot paint through one that covers it. Shrinking the
    // panel to the part still showing the old colour is what lets the new colour
    // come through everywhere else.
    Point[] WipePolygon(double t, int w, int h)
    {
        // Ease-in-out so the sweep does not start on a jolt.
        double p = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        if (p >= 1.0) return null;
        if (p <= 0.5)
        {
            // The dividing line still crosses the left and top edges.
            return new Point[]
            {
                new Point((int)Math.Round(2 * p * w), 0),
                new Point(w, 0),
                new Point(w, h),
                new Point(0, h),
                new Point(0, (int)Math.Round(2 * p * h)),
            };
        }
        // Past the middle the top-left corner is gone entirely and the line is
        // closing on the bottom-right one.
        return new Point[]
        {
            new Point(w, (int)Math.Round((2 * p - 1) * h)),
            new Point(w, h),
            new Point((int)Math.Round((2 * p - 1) * w), h),
        };
    }

    void WipeFrame()
    {
        double t = wipeSw == null ? 1.0 : wipeSw.Elapsed.TotalMilliseconds / 300.0;
        if (t > 1.0) t = 1.0;
        bool done = t >= 1.0;
        if (wipePanel != null)
        {
            if (done)
            {
                wipePanel.Visible = false;
                Region old = wipePanel.Region;
                wipePanel.Region = null;
                if (old != null) old.Dispose();
            }
            else
            {
                Point[] polygon = WipePolygon(t, Math.Max(1, wipePanel.Width), Math.Max(1, wipePanel.Height));
                Region previous = wipePanel.Region;
                // Region has no polygon constructor on .NET Framework, so the sweep
                // is built as a path first.
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(polygon);
                    wipePanel.Region = new Region(path);
                }
                if (previous != null) previous.Dispose();
                wipePanel.Invalidate();
            }
        }
        if (!done) return;
        wiping = false;
        wipeSw = null;
        Invalidate(true);
        if (!exiting && settled && !gliding) StopPump();
    }

    // --- deck ---

    // The pile always reads the same way round: the card that has been alive
    // longest owns the anchored end and is drawn in full, and every card that
    // arrived after it is tucked behind and one step further in. What the
    // direction setting changes is only which screen edge that end is on, and
    // that is settled once in the constructor, so nothing here has to branch.

    // Slot 0 is the oldest card alive and owns the front of the deck: the fully
    // drawn, anchored position. Every later card is tucked behind it and one step
    // further from the anchored edge. A file whose owning process is gone is
    // swept here too, so the stack cannot grow phantom slots.
    int SlotY()
    {
        // A preview never counts the deck it is shown next to: the settings page
        // keeps one stack of its own and closes the last stack before raising the
        // next, so a preview's rung comes from the argument, not from the
        // directory. It still sweeps the dead - a preview whose host went away
        // must not leave a file behind - but it leaves the living alone, because
        // deleting a file here would not close that card, it would only orphan it.
        if (opt.Preview)
        {
            sweepPreviewDeck();
            int rung = Math.Max(0, opt.PreviewSlot);
            // Same cap as a real deck, so the preview answers "how many fit here"
            // with the same number the corner would really take.
            int cap = StackLimit(opt.MaxStack, area, Height, slotOffset);
            if (rung > cap - 1) rung = cap - 1;
            deckSlot = rung;
            prevHwnd = previewBelowHwnd(rung);
            if (topCorner)
                return Math.Min(area.Bottom - Height, slotBase + rung * slotOffset);
            return (int)Math.Max(area.Top, slotBase - rung * slotOffset);
        }

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
        deckSlot = index;

        // Who this card is tucked behind: the one above it in the deck, which is
        // always the older card, whichever way the pile grows. A card with nobody
        // above it is the front of the stack and goes to the top of the Z order.
        int above = index - 1;
        prevHwnd = IntPtr.Zero;
        if (above >= 0 && above < live.Count)
        {
            try
            {
                string raw = File.ReadAllText(live[above]).Trim();
                long prev;
                if (long.TryParse(raw, out prev) && prev != 0) prevHwnd = new IntPtr(prev);
            }
            catch { }
        }
        if (topCorner)
            return Math.Min(area.Bottom - Height, slotBase + index * slotOffset);
        return (int)Math.Max(area.Top, slotBase - index * slotOffset);
    }

    // Preview decks are swept, never trimmed: a preview must be able to leave a
    // file behind if its host dies, but it must never close a sibling that is
    // still drawing. The settings page kills the whole stack before raising the
    // next one, so the living siblings are exactly the cards on screen.
    void sweepPreviewDeck()
    {
        try
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (string f in Directory.GetFiles(cardDir, "*.card"))
            {
                if (f == cardFile) continue;
                string[] bits = Path.GetFileNameWithoutExtension(f).Split('-');
                int ownerPid = 0;
                if (bits.Length >= 2) int.TryParse(bits[1], out ownerPid);
                if (ownerPid != 0 && ownerPid != self && !Native.ProcessAlive(ownerPid))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    // The card one rung in front of this one, so the stack gathers in age order
    // the same way a real deck does. The rung is in the file name because the
    // rungs are raised in one burst and their creation order says nothing, and
    // the tag narrows the lookup to this stack: during a swap the previous stack
    // is still on disk, and its rung is not this card's rung.
    IntPtr previewBelowHwnd(int rung)
    {
        // Which rung this card is tucked behind: always the older one, whichever
        // way the pile grows. No counterpart means this card is the front of the
        // stack, and zero is the top of the Z order.
        int below = rung - 1;
        if (below < 0) return IntPtr.Zero;
        try
        {
            string[] under = Directory.GetFiles(cardDir, "*-" + cornerKey + "p" + below + "s" + opt.PreviewStamp + ".card");
            if (under.Length == 0) return IntPtr.Zero;
            long prev;
            if (long.TryParse(File.ReadAllText(under[0]).Trim(), out prev) && prev != 0)
                return new IntPtr(prev);
        }
        catch { }
        return IntPtr.Zero;
    }

    // A preview stack is replaced by a newer one, and the page cannot be trusted
    // to close it first: the request that raises the next stack and the cards it
    // replaces are different processes, and the page fires on every drag of a
    // slider. So every preview watches for the stack that superseded it and
    // leaves on its own, which is what makes an edit look like the card changing
    // instead of the card being closed and another one opened.
    //
    // A stack is superseded by a higher tag, and only by one that has already put
    // its own files in the directory - an empty directory means the page closed
    // the preview outright, and that must not be mistaken for a swap. The search
    // spans every rung of the corner, not just this card's own: a stack that
    // shrinks has no counterpart for the rungs it dropped, and those rungs would
    // otherwise stay on screen forever.
    bool previewSuperseded()
    {
        if (opt.PreviewStamp.Length == 0) return false;
        try
        {
            string[] mine = Directory.GetFiles(cardDir, "*-" + cornerKey + "p*s*.card");
            if (mine.Length == 0) return false;
            foreach (string f in mine)
            {
                if (f == cardFile) continue;
                string tag = Path.GetFileNameWithoutExtension(f);
                int at = tag.LastIndexOf('s');
                if (at < 0) continue;
                string other = tag.Substring(at + 1);
                if (other.Length == 0) continue;
                if (string.CompareOrdinal(other, opt.PreviewStamp) > 0) return true;
            }
        }
        catch { }
        return false;
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
        // A preview has neither an answer to wait for nor a reader to watch for:
        // it exists to be looked at while the settings page is open. Its two exits
        // are the page going away and a newer stack taking its place.
        if (opt.Preview)
        {
            if (previewSuperseded()) { BeginExit(); return; }
            if (opt.OwnerPid > 0 && !Native.ProcessAlive(opt.OwnerPid)) BeginExit();
            return;
        }
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

    // Text is drawn with GDI+ into an off-screen buffer, then every buffered
    // pixel's coverage is read back and mapped onto the card's own two colours.
    //
    // The buffer is drawn in GREY, not ClearType. ClearType puts each stem's
    // coverage on one subpixel column, so the same stem reads 77, 151 or 28
    // depending only on which column it landed on - collapsing that to luminance
    // leaves neighbouring stems disagreeing by up to 5x. That is the "pixels not
    // even" complaint, and it is the one thing ClearType cannot be tuned out of:
    // it is what the technique is. Grey is a true per-pixel area, so no column can
    // bias a stem. Measured at 13-18px the stem-to-stem spread goes 0.11-0.13 ->
    // 0.05-0.08 and the share of hard pixel-to-pixel jumps 0.38-0.48 -> 0.25-0.28.
    //
    // Drawing white on black is what makes the readback cheap: the luminance of
    // a buffered pixel is the glyph's coverage, so mapping it back is one
    // multiply-add per channel. Measuring still goes through MeasureGraphics();
    // GDI+ returns identical metrics for both hints, so the wrap computed there
    // is the wrap the ink pass draws.
    //
    // The buffer is drawn at InkScale times the card's pixel size and resolved
    // back down by averaging the samples that fall inside each pixel. That average
    // is the glyph's true area coverage, and area coverage is what makes the
    // renderer independent of where a glyph happens to land on the pixel grid: a
    // third-of-a-pixel shift moves the output by 20%, against 37% at one sample
    // per axis. Three and four samples were measured and are worse, because GDI+
    // grid-fits the glyph before it rasterises, so a larger factor buys a finer
    // snap rather than a truer integral. The cost is about 1ms for the whole body
    // box against a card that rebuilds on a 40ms beat.
    //
    // NoWrap because every string is pre-wrapped, and NoClip so a glyph's
    // overhang is not cut at the layout box the way GDI's clipping would.
    static StringFormat CreateLayout()
    {
        StringFormat f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
        f.Trimming = StringTrimming.None;
        return f;
    }

    // How tall the strip a pile reveals has to be, in device pixels.
    //
    // Measured from the ink, not from the band. A band is sized for the largest
    // type the dial can ask for, so at the default size it carries a fifth of its
    // height as slack the headline does not use; charging that slack to the strip
    // is what put a gap of blank surface between one card's headline and the next
    // card's clock. The headline hangs off the revealed edge instead, so its own
    // slack drops behind the card in front, and only the air above the clock and
    // one hairline under the headline are counted.
    //
    // Each line is charged its line spacing, not the height of the box it is
    // painted into. A box is sized for the largest type the dial can ask for, so
    // at the default size it stands about a third taller than the glyphs in it,
    // and that third is the surface the reader saw between one headline and the
    // next card's clock. LineHigh is the box; BodyLinePx is the type.
    //
    // Before this the strip charged the full band: metaHeaderTop + metaRowHigh +
    // 2 + captionHigh + 4 for a bottom corner, metaRowHigh + 2 + captionHigh + 8
    // for a top one. At 448px wide with the default type that is 82 pixels; it is
    // 55 from the type, and the pile reads as one block instead of cards drifting
    // apart.
    int StripHeight()
    {
        int metaInk = Math.Max(Px(17), BodyLinePx(overlineFont));
        int captionInk = Math.Max(Px(19), BodyLinePx(captionFont));
        return headerAtTop
            ? Px(3) + metaInk + Px(2) + captionInk + Px(3)
            : metaInk + Px(3) + captionInk + Px(5);
    }

    // One line in a fixed-width box: trimmed with an ellipsis rather than cut
    // mid-glyph, and centred in the box's height so a short line sits in the
    // middle of its band instead of on top of it.
    //
    // The headline is the exception: it is pinned to the edge its band hangs from
    // (see TextBoxHang), because its band is sized for the largest type the dial
    // can ask for and the slack that leaves has to fall away from the edge a pile
    // reveals, where the card in front covers it. Centring the headline put that
    // slack on both sides, which is half of the gap the reader saw between one
    // card's headline and the next card's clock.
    static StringFormat CreateBox(StringAlignment align, StringAlignment line)
    {
        StringFormat f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.NoWrap;
        f.Trimming = StringTrimming.EllipsisCharacter;
        f.Alignment = align;
        f.LineAlignment = line;
        return f;
    }

    static readonly StringFormat TextLayout = CreateLayout();
    static readonly StringFormat TextBoxLeft = CreateBox(StringAlignment.Near, StringAlignment.Center);
    static readonly StringFormat TextBoxRight = CreateBox(StringAlignment.Far, StringAlignment.Center);
    static readonly StringFormat TextBoxHang = CreateBox(StringAlignment.Near, StringAlignment.Near);
    static readonly StringFormat TextBoxHangUp = CreateBox(StringAlignment.Near, StringAlignment.Far);

    // GDI+ measures against a Graphics and there is none outside a paint. One
    // 1x1 bitmap is enough to ask it, and it is kept because laying the body out
    // measures once per word of the notice.
    static Graphics measureGfx;

    static Graphics MeasureGraphics()
    {
        if (measureGfx == null)
        {
            Graphics g = Graphics.FromImage(new Bitmap(1, 1));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            measureGfx = g;
        }
        return measureGfx;
    }

    // --- the ink pass ---
    //
    // Text is drawn white on black into one of these scratch bitmaps, then the
    // coverage of each pixel is read back and mapped between the surface colour
    // and the ink colour. White-on-black is what makes that readback cheap: the
    // luminance of a buffered pixel *is* the glyph's coverage, so no unpremultiply
    // and no per-glyph alpha is needed.
    //
    // The bitmaps are kept per size because a card repaints its body on every
    // scroll frame and reallocating a 372x112 surface each time is the only
    // allocation this path would otherwise add. Per card, not static: a stack of
    // cards each paints on its own thread, and one shared surface would have two
    // of them filling and reading the same pixels.
    // One scratch surface per size, with the readback buffer it is copied into:
    // the buffer is as large as the bitmap, so allocating it per frame would put
    // a quarter-megabyte on the heap every time the body scrolls.
    sealed class InkSurface
    {
        internal Bitmap Bits;
        internal Bitmap Small;
        internal int[] Pixels;
        internal int[] Samples;
    }

    readonly Dictionary<long, InkSurface> inkSurfaces = new Dictionary<long, InkSurface>();

    Graphics InkStart(int w, int h, out InkSurface buf)
    {
        buf = null;
        if (w < 1 || h < 1) return null;
        long key = ((long)w << 32) | (uint)h;
        if (!inkSurfaces.TryGetValue(key, out buf))
        {
            buf = new InkSurface();
            buf.Bits = new Bitmap(w * InkScale, h * InkScale, PixelFormat.Format32bppArgb);
            buf.Small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            buf.Pixels = new int[w * h];
            buf.Samples = new int[w * InkScale * h * InkScale];
            inkSurfaces[key] = buf;
        }
        Graphics g = Graphics.FromImage(buf.Bits);
        // Every pixel of the buffer is mapped back, so the backdrop has to be the
        // bottom of the ramp rather than transparency.
        g.Clear(Color.Black);
        // AntiAliasGridFit, not ClearType. Grey coverage is a per-pixel area, so no
        // stem can be biased by which subpixel column it landed on.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        // Default, not Half. Half pushes every glyph a half pixel right, which
        // straddles two subpixel columns instead of sitting on one; measured at
        // 13px it lifted the stem-to-stem spread from 0.133 to 0.156.
        g.PixelOffsetMode = PixelOffsetMode.Default;
        // Callers keep drawing in card pixels; this is the only place the extra
        // samples exist.
        g.ScaleTransform(InkScale, InkScale);
        return g;
    }

    // Coverage -> coverage, one entry per possible byte.
    //
    // The lift here is a gamma, not an S-curve. An S-curve does raise the solid
    // share, but that metric is exactly what a binary mask maximizes: pushing the
    // midtones to the two ends turns every ramp into a step, which is the
    // "jagged edge" half of the complaint, and it multiplies any coverage
    // difference between neighbouring stems - a stem at 0.60 goes to 0.78 while
    // its neighbour at 0.40 goes to 0.22. Measured over 13-18px, the S-curve at
    // 2.6 doubled the share of hard pixel-to-pixel jumps (0.38 -> 0.51) and left
    // the stem-to-stem spread at 0.16 against 0.12 for a plain gamma.
    //
    // 0.40 is what the grey buffer needs: the box average costs about a third of
    // the ink weight, and this puts it back without pushing the ramp back into
    // steps. It is monotone, so a ramp stays a ramp.
    static readonly byte[] Coverage = BuildCoverage();

    const double CoverageGamma = 0.40;

    static byte[] BuildCoverage()
    {
        byte[] tab = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double a = i / 255.0;
            double v = Math.Pow(a, CoverageGamma);
            int b = (int)Math.Round(v * 255);
            tab[i] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        }
        return tab;
    }

    // Reads the coverage back and paints the result onto the real surface.
    void InkCommit(Graphics g, InkSurface buf, Color ink, int dx, int dy)    {
        if (buf == null) return;
        int sR = surface.R, sG = surface.G, sB = surface.B;
        int iR = ink.R, iG = ink.G, iB = ink.B;
        int flat = surface.ToArgb();
        int solid = ink.ToArgb();
        Bitmap bits = buf.Small;
        int[] px = buf.Pixels;
        int[] samples = buf.Samples;
        // Resolve the supersampled buffer onto the card's own pixel grid by
        // averaging the samples that fall inside each pixel. A filtered
        // DrawImage was here first and measured worse: it blends from samples
        // that straddle the pixel border, so every stem kept a grey skirt about
        // two pixels wide, and that skirt is exactly the bloom this had to lose.
        BitmapData src = buf.Bits.LockBits(new Rectangle(0, 0, buf.Bits.Width, buf.Bits.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(src.Scan0, samples, 0, samples.Length);
        }
        finally
        {
            buf.Bits.UnlockBits(src);
        }
        int n = InkScale;
        int area = n * n;
        int stride = buf.Bits.Width;
        for (int y = 0; y < bits.Height; y++)
        {
            int front = y * bits.Width;
            for (int x = 0; x < bits.Width; x++)
            {
                int r = 0, gr = 0, bl = 0;
                for (int sy = 0; sy < n; sy++)
                {
                    int row = (y * n + sy) * stride + x * n;
                    for (int sx = 0; sx < n; sx++)
                    {
                        int c = samples[row + sx];
                        r += (c >> 16) & 0xFF;
                        gr += (c >> 8) & 0xFF;
                        bl += c & 0xFF;
                    }
                }
                // 77/151/28 is the same weight GDI+ uses to grade a coverage
                // pixel; without the shift it is a 16-bit sum.
                int cov = (((r / area) * 77 + (gr / area) * 151 + (bl / area) * 28) >> 8);
                if (cov <= 0) { px[front + x] = flat; continue; }
                if (cov >= 255) { px[front + x] = solid; continue; }
                cov = Coverage[cov];
                px[front + x] = unchecked((int)0xFF000000)
                    | (sR + (iR - sR) * cov / 255) << 16
                    | (sG + (iG - sG) * cov / 255) << 8
                    | (sB + (iB - sB) * cov / 255);
            }
        }
        BitmapData data = bits.LockBits(new Rectangle(0, 0, bits.Width, bits.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(px, 0, data.Scan0, px.Length);
        }
        finally
        {
            bits.UnlockBits(data);
        }
        g.DrawImageUnscaled(bits, dx, dy);
    }

    // One line in a box: draw, then commit.
    void InkText(Graphics g, string text, Font font, Color ink, RectangleF box, StringFormat format)
    {
        InkSurface buf;
        using (Graphics scratch = InkStart((int)Math.Ceiling(box.Width), (int)Math.Ceiling(box.Height), out buf))
        using (SolidBrush brush = new SolidBrush(Color.White))
        {
            if (scratch == null) return;
            scratch.DrawString(text, font, brush, new RectangleF(0, 0, box.Width, box.Height), format);
        }
        InkCommit(g, buf, ink, (int)box.X, (int)box.Y);
    }

    // Wake the scrollbar: full opacity now, and the idle clock restarted so it
    // stays up for a moment after the text stops instead of leaving mid-drag.
    void BarAwake()
    {
        if (barAlpha >= 0.996) barSw.Restart();
        else barAlpha = 1.0;
        if (scrollTimer != null && !scrollTimer.Enabled) scrollTimer.Start();
        if (bodyBox != null) bodyBox.Invalidate();
    }

    void PaintBody(Graphics g)
    {
        // Draw from the first line that is partly visible, offset by the
        // fractional remainder, so the text moves by whole pixels instead of
        // jumping a row.
        int first = Math.Max(0, (int)Math.Floor(scroll / lineHeight));
        int offset = (int)Math.Round(scroll - first * lineHeight);
        int last = Math.Min(bodyLines.Count, first + (int)Math.Ceiling((double)bodyHeight / lineHeight) + 1);
        // The whole visible body goes through one scratch surface and one
        // readback: doing it per line would clear and lock the same buffer six
        // times a frame for no visible gain.
        InkSurface buf;
        using (Graphics scratch = InkStart(bodyWidth, bodyHeight, out buf))
        {
            if (scratch != null)
            {
                using (SolidBrush brush = new SolidBrush(Color.White))
                {
                    for (int i = first; i < last; i++)
                    {
                        int y = (i - first) * lineHeight - offset;
                        // A line that would run past the bottom of the box is left out
                        // whole. Half a row of glyphs sliced through the middle reads as
                        // a rendering fault, and at the top of the type dial it is the
                        // only row that can be cut, since the box is as tall as it
                        // always was.
                        if (y + lineHeight > bodyHeight) break;
                        scratch.DrawString(bodyLines[i], bodyFont, brush, new PointF(0, y), TextLayout);
                    }
                }
                InkCommit(g, buf, bodyColor, 0, 0);
            }
        }
        if (maxScroll > 0 && barAlpha > 0.004)
        {
            // A hairline track with a proportionally sized thumb, like a real bar.
            // Both fade together: the track on its own is the grey stripe the fade
            // exists to get rid of.
            int alpha = (int)Math.Round(255 * barAlpha, MidpointRounding.AwayFromZero);
            using (SolidBrush t = new SolidBrush(Fade(trackColor, alpha)))
                g.FillRectangle(t, new Rectangle(bodyWidth + Px(3), 0, Px(2), bodyHeight));
            int thumbY = (int)((bodyHeight - thumbHeight) * scroll / maxScroll);
            using (SolidBrush t = new SolidBrush(Fade(thumbColor, alpha)))
                g.FillRectangle(t, new Rectangle(bodyWidth + Px(2), thumbY, Px(4), thumbHeight));
        }
    }

    // The same colour at a fraction of its opacity. Scrollbar and countdown both
    // use it, and both are drawn over the surface rather than composited by the
    // window, so the alpha has to be baked into the brush.
    static Color Fade(Color c, int alpha)
    {
        if (alpha < 0) alpha = 0;
        if (alpha > 255) alpha = 255;
        return Color.FromArgb(alpha, c.R, c.G, c.B);
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
        // The resting wash and the hover wash, in the same grey the palette already
        // uses for a pressed surface. The rest has to be barely there: at a fifth of
        // the hover it reads as a hint of a hit area, where a stronger tile turned a
        // pair of quiet glyphs into a toolbar sitting on the title row.
        int restingAlpha = dark ? 0x0A : 0x06;
        int hoverAlpha = hoverColor.A;
        p.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // A resting wash, then the hover one on top of it. A hairline glyph
            // with nothing behind it reads as decoration; a faint tile behind every
            // button says "this is a control" before the pointer ever arrives, and
            // the hover state only has to make it more so.
            if (restingAlpha > 0 || hover)
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
                    using (SolidBrush fill = new SolidBrush(Fade(hoverColor, hover ? hoverAlpha : restingAlpha)))
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

    // Lay the body out in the current font and box. Split out of the constructor
    // because a type change has to run exactly the same measurement again - the
    // line breaks, the line height and the scroll range all follow the type, and
    // a second copy of this arithmetic would drift from the first.
    void MeasureBody(string text)
    {
        lineHeight = Math.Max(1, BodyLinePx(bodyFont));
        bodyLines = SplitLines(text, bodyFont, bodyWidth);
        int totalHeight = bodyLines.Count * lineHeight;
        maxScroll = Math.Max(0, totalHeight - bodyHeight);
        thumbHeight = Math.Max(Px(20), (int)((long)bodyHeight * bodyHeight / Math.Max(1, totalHeight)));
        if (scroll > maxScroll) { scroll = maxScroll; scrollTarget = maxScroll; }
    }

    // Re-letter the card in place for a new type size. Nothing here moves the
    // window: the card keeps its width, its height and its bands, and only the
    // glyphs inside them change size, which is what a font size means. The three
    // Font objects are rebuilt at whole pixel sizes, the body is measured again in
    // the new metrics, and the footer rows and the caption are re-drawn.
    internal void ReType(int wanted)
    {
        int next = Math.Max(50, Math.Min(250, wanted));
        if (next == fontSize) return;
        fontSize = next;
        fontScale = next / 100f;

        int textPx = Math.Max(15, (int)Math.Round(15 * unit * fontScale, MidpointRounding.AwayFromZero));
        // The headline keeps its one-step lead over the body, exactly as the
        // constructor sizes it; using textPx here would flatten the rank back out
        // the first time the reader touched the type dial.
        int titlePx = Math.Max(18, (int)Math.Round(20 * unit * fontScale, MidpointRounding.AwayFromZero));
        int metaPx = Math.Max(12, (int)Math.Round(12 * unit * fontScale, MidpointRounding.AwayFromZero));
        // Keep the old ones to dispose after the swap: the paint handlers read these
        // fields, so replacing them first and disposing second cannot leave a frame
        // drawing with a dead handle.
        Font oldCaption = captionFont, oldBody = bodyFont, oldMeta = metaFont;
        captionFont = new Font(heavyFace, titlePx, heavyStyle, GraphicsUnit.Pixel);
        bodyFont = new Font(bodyFace, textPx, FontStyle.Regular, GraphicsUnit.Pixel);
        metaFont = new Font(uiFont, metaPx, FontStyle.Regular, GraphicsUnit.Pixel);
        // A drag is dozens of notches and each one builds three faces; without this the
        // GDI handles pile up until the collector catches them.
        try { oldCaption.Dispose(); oldBody.Dispose(); oldMeta.Dispose(); } catch { }
        MeasureBody(opt.Body);
        Reslot();
        Invalidate(true);
        for (int i = 0; i < Controls.Count; i++)
        {
            Control c = Controls[i];
            if (c is BufferedPanel) c.Invalidate();
        }
    }

    // The strip a pile reveals is measured from the type the card is drawn with,
    // so a live type change has to re-measure it and re-place this card in the
    // deck. Everything downstream reads slotOffset and targetY - SlotY, Reflow,
    // the entrance - so recomputing these two is all it takes for the whole pile
    // to tighten up while the dial is being dragged.
    internal void Reslot()
    {
        slotOffset = StripHeight();
        targetY = SlotY();
        // Only after the entrance has finished: mid-animation the pump owns the
        // window's Y and this would snap it to the slot one frame early.
        if (settled) Location = new Point(targetX, targetY);
    }

    // A footer row is a bare strip of paint: no border, no background, and it
    // never swallows a click, so the card underneath still dismisses. `right`
    // aligns the text to the strip's far edge, which is what the session name
    // wants: it is the variable-length half of the line.
    void AddFooterRow(int top, int left, int width, string text, bool right, Font font)
    {
        BufferedPanel row = new BufferedPanel();
        // `top` arrives in device pixels: the caller lays the rows out from the
        // type-scaled band above them, so scaling it again here would move the
        // second row further down than the first one's height.
        row.Location = new Point(Px(left), top);
        // The row is as tall as one line of the face it paints with, never less
        // than the designed grid line: at the top of the type dial a fixed 20-unit
        // row cut the clock and the permission name in half.
        row.Size = new Size(Px(width), Math.Max(Px(20), LineHigh(font ?? metaFont)));
        row.BackColor = surface;
        row.Paint += delegate(object s, PaintEventArgs e)
        {
            // Same ink pass as the rest of the card, which is what makes the
            // smallest type on it stop looking grainy. Trimmed with an ellipsis
            // so a long session name ends in "..." instead of running under the
            // corner buttons.
            // The paint surface, not the control's own Size: with per-monitor DPI
            // the two disagree, and a right edge taken from Size puts the text
            // outside the surface, where it simply is not drawn.
            InkText(e.Graphics, text, font ?? metaFont, mutedColor, e.Graphics.VisibleClipBounds, right ? TextBoxRight : TextBoxLeft);
        };
        row.Click += delegate { Dismiss(); };
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

    // Measured with GDI+, because GDI+ is what draws. The two engines do not
    // agree on the width of the same string - different shaping, different
    // hinting, about five percent apart - so measuring with one and drawing with
    // the other either wraps a line that had room or clips one that did not. The
    // same StringFormat goes to both, so they see exactly the same metrics.
    static int TextWidth(string text, Font font)
    {
        return (int)Math.Round(MeasureGraphics().MeasureString(text, font, int.MaxValue, TextLayout).Width);
    }

    // One line box of a face, from the engine that will draw it. Every band on the
    // card is sized with this: a band measured in fixed grid units clips the glyphs
    // as soon as the type dial passes the size that band was drawn for.
    static int LineHigh(Font font)
    {
        return (int)Math.Ceiling(MeasureGraphics().MeasureString("Ag中", font, int.MaxValue, TextLayout).Height);
    }

    // The distance between two baselines of running text, in whole pixels. The
    // font's own line box is generous - Noto Sans SC asks 1.45em - and stacking
    // lines that far apart is what made a paragraph read as scattered lines rather
    // than as a block. This is the tighter leading a system surface uses, snapped
    // to a whole pixel so the tenth line sits exactly where the first predicted it.
    static int BodyLinePx(Font font)
    {
        return Math.Max(1, (int)Math.Round(font.Size * BodyLeading, MidpointRounding.AwayFromZero));
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
        // appears in its slot, fully opaque, on the first frame. A preview is the
        // exception, because it exists to show what the card does - a reader who
        // turned motion off system-wide still opened this page to look at it. The
        // settings page's own rebuilds ask for the same thing explicitly, since
        // replaying the entrance on every keystroke would be a light show.
        if ((!animated && !opt.Preview) || opt.Instant)
        {
            Opacity = 1.0;
            Location = new Point(targetX, targetY);
            SettleStyles();
            settled = true;
            StopPump();
            return;
        }
        double t = Math.Min(1.0, (entranceSw.Elapsed.TotalMilliseconds + entranceSkewMs) / 420.0);
        double ease = 1 - Math.Pow(1 - t, 3);
        // Every card in a preview stack fades in on the same curve, and a card
        // behind the front one travels only the one slot that separates them: they
        // are raised in the same instant, so anything else reads as the stack
        // turning over instead of arriving. Being drawn over by the card in front is
        // what makes it read as coming out from behind rather than dropping onto it.
        // Which card that is cannot be worked out from its own slot, so the stack
        // tells it.
        if (opt.Preview && !opt.DeckFront)
        {
            Opacity = ease;
            int lift = (int)Math.Round((topCorner ? -1 : 1) * slotOffset * (1 - ease));
            Location = new Point(targetX, targetY + lift);
            if (t < 1.0) return;
            SettleStyles();
            settled = true;
            StopPump();
            return;
        }
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

    // Slot n of a preview stack belongs behind slot n-1, and a window shown later
    // is on top of one shown earlier, so the order has to be stated rather than
    // inherited. Doing it here, at the moment the card is raised, means the
    // entrance that follows is already drawn underneath the card it slides out
    // from; waiting for the reflow tick would animate it on top and then drop it
    // behind, which reads as the stack swapping.
    internal void TuckBehind(IntPtr front)
    {
        if (front == IntPtr.Zero || !Native.IsWindow(front)) return;
        try
        {
            Native.SetWindowPos(Handle, front, 0, 0, 0, 0,
                Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOMOVE);
        }
        catch { }
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

    // Every close path funnels here. A preview card is not the page's to close,
    // though: the page owns the stack, so a click reports the slot and the host
    // puts the next kind in it. That is why this cannot just call BeginExit.
    void Dismiss()
    {
        if (opt.OnPreviewDismiss != null)
        {
            opt.OnPreviewDismiss(opt.PreviewSlot);
            return;
        }
        BeginExit();
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
        if (scrollTimer != null) scrollTimer.Stop();
        exitFromOpacity = Opacity;
        exitFromY = Location.Y;
        exitSw = Stopwatch.StartNew();
        StartPump(4);
    }

    // One tick for the one thing that moves slower than a frame: the scrollbar's
    // fade. It runs while there is anything to fade and parks itself after.
    void ScrollTick()
    {
        // The bar is present while the text is moving and gone a moment after it
        // stops, so the right edge goes back to being type instead of a grey
        // stripe that outlives the reason it appeared.
        if (maxScroll > 0)
        {
            bool moving = dragging || gliding;
            if (moving) barSw.Restart();
            double alpha = 1.0;
            if (!moving)
            {
                double idle = barSw.Elapsed.TotalMilliseconds - BarHoldMs;
                if (idle > BarFadeMs) alpha = 0.0;
                else if (idle > 0) alpha = 1.0 - idle / BarFadeMs;
            }
            if (Math.Abs(alpha - barAlpha) > 0.004)
            {
                barAlpha = alpha;
                bodyBox.Invalidate();
            }
        }

        // Nothing left to watch: the timer parks itself until something asks for
        // it again, exactly like the frame pump does.
        if (maxScroll <= 0 || barAlpha <= 0) scrollTimer.Stop();
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
        // A stack raised in one go shares a start time, so the rungs that were
        // built later are already part way through the entrance on their first
        // frame instead of fading in one behind the other.
        if (opt.EntranceEpoch > 0)
        {
            double late = (DateTime.UtcNow.Ticks - opt.EntranceEpoch) / (double)TimeSpan.TicksPerMillisecond;
            if (late > 0) entranceSkewMs = late;
        }
        StartPump(4);
        // An approval card has no life timer: it is supposed to stay until the
        // answer arrives, and only the dismiss file ends it (with the watchdog as
        // backstop). A preview stays for the same reason - the settings page owns
        // it and replaces it, so a timer here would fight the page.
        if (opt.DismissFile.Length == 0 && !opt.Preview) life.Start();
        reflow.Start();
        if (watch != null) watch.Start();
        // The bar starts visible on a card that has more text than fits, then
        // fades on its own: the reader gets told there is more to read once, and
        // the right edge is quiet from then on. The idle clock starts here rather
        // than at construction, otherwise a slow entrance would eat the hold.
        barSw.Restart();
        if (maxScroll > 0) scrollTimer.Start();
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
        if (scrollTimer != null) scrollTimer.Dispose();
        // Leaving the slot file behind would keep this card's place in the stack
        // for good, so the next card would never reclaim the corner.
        try { File.Delete(cardFile); } catch { }
    }
}

// The preview host: one process that owns the whole preview stack and rebuilds
// it in place as the settings page edits. Starting a process costs about 300ms
// (CLR, WinForms, fonts, the shell showing a window), while rebuilding a stack
// inside a process that is already up costs about 40ms. That difference is the
// whole difference between a preview that trails the slider and one that keeps
// up with it, so the page talks to this instead of starting a stack per edit.
sealed class PreviewHost : ApplicationContext
{
    const int MaxCards = 12;

    readonly Options baseOptions;
    readonly string specPath;
    readonly Timer poll;
    // One tag for the whole host, not one per edit: every card in the stack has
    // to be able to find the card in front of it by file name, and cards that
    // survive an edit were built under an older tag. A per-edit tag would break
    // that lookup and send every third card back to the top of the stack.
    readonly string hostStamp = DateTime.UtcNow.Ticks.ToString("D19");
    List<Card> live = new List<Card>();
    string lastSpec = null;
    // Two hosts can be raised on the same spec - a plugin reload keeps the same
    // DSH process, so the older host's owner check still passes - and then both
    // build the same rungs: each closes the other's cards as superseded while
    // both keep spawning, which is a stack that flickers and a maxStack that
    // comes up with the wrong number of cards. A claim file next to the spec
    // settles it: a host writes its own tag on start and steps aside as soon as
    // the claim names someone else, so the newest host is the only one left.
    readonly string claimPath;
    // Set by a click on a preview card, consumed by the next Sync: which slot was
    // closed, so the stack can put a different kind in it.
    int pendingSlot = -1;
    // A click changes the stack without changing the spec, so the poll cannot see
    // it. Rather than rebuilding a card from inside that card's own click handler
    // - the handler would then be running on a disposed form - the click only
    // raises this flag and the poll does the work 25ms later.
    bool dirty;
    // Which slot is showing a stand-in kind, and which one. Kept across rebuilds
    // so a later colour edit does not silently put the closed kind back; dropped
    // as soon as the page sends a different set of kinds, because then the page
    // itself is the thing that changed.
    readonly Dictionary<int, string> swapped = new Dictionary<int, string>();
    string swappedFor = null;

    public PreviewHost(Options options)
    {
        // The host's own spec path must not leak into the cards it makes, or a
        // child would try to become a host in turn.
        baseOptions = options.Clone();
        baseOptions.PreviewSpec = "";
        specPath = options.PreviewSpec;
        claimPath = specPath + ".host";
        // Take the claim before the first Pump, so a host that was already up sees
        // a name that is not its own on its very next tick and leaves.
        try { File.WriteAllText(claimPath, hostStamp); } catch { }
        // A timer tick, not a file watcher: a watch event arrives on a pool
        // thread, and a Form shown off its own thread gets a message queue
        // nothing pumps, so the card paints once and then ignores its own exit.
        // The whole gain of the host is that a rebuild is ~40ms, so a 25ms poll
        // is not what stands between a slider and its preview.
        poll = new Timer();
        poll.Interval = 25;
        poll.Tick += delegate { Pump(); };
        poll.Start();
        Pump();
    }

    void Pump()
    {
        if (baseOptions.OwnerPid > 0 && !Native.ProcessAlive(baseOptions.OwnerPid))
        {
            Shutdown();
            return;
        }
        string text;
        try
        {
            // The page deletes the spec when it closes the preview. A host with
            // nothing to show has no reason to live.
            if (!File.Exists(specPath)) { Shutdown(); return; }
            text = File.ReadAllText(specPath);
            // Someone else claimed this preview after this host started. Leaving
            // is the whole point of the claim: two hosts would otherwise fight
            // over the same rungs. A missing claim is re-taken rather than read as
            // a rival, because the page deletes it along with the spec and the
            // next preview must not find the corner held by nobody.
            if (!File.Exists(claimPath)) File.WriteAllText(claimPath, hostStamp);
            string owner = File.ReadAllText(claimPath);
            if (owner.Trim() != hostStamp) { Shutdown(); return; }
        }
        catch { return; }
        if (text == lastSpec && !dirty) return;
        dirty = false;
        lastSpec = text;
        Sync(text);
    }

    // Pick the spec up one card at a time instead of replacing the stack whole.
    // Replacing it is how this started, and it works, but it throws away the cards
    // that did not change: a colour edit then flickers the entire stack, and a
    // count edit has no card left to show arriving. A card whose geometry is
    // unchanged is kept and recoloured in place; only cards that have to move are
    // rebuilt.
    void Sync(string text)
    {
        Dictionary<string, string> spec = ParseSpec(text);
        // One card per kind the page has switched on, and the pile stays as tall as
        // "cards per corner" says whatever is ticked. A closed card or a switch turned
        // off both have to leave a full pile, so the vacancies are filled by the kinds
        // that are still on, round and round. With nothing on there is nothing to fill
        // with and every rung is a blank card - numbered, and each with its own clock,
        // so three blanks still read as three cards.
        List<string> on = PreviewKinds(spec);
        int size = ParseCount(spec);
        List<string> kinds = new List<string>();
        for (int i = 0; i < size; i++)
            kinds.Add(on.Count == 0 ? "" : on[i % on.Count]);
        int count = Math.Max(1, Math.Min(MaxCards, kinds.Count));

        string fingerprint = string.Join(",", kinds.ToArray());
        if (fingerprint != swappedFor)
        {
            // The page changed which kinds it wants, so every stand-in is stale: a
            // card that was swapped in for a closed one must not survive the reader
            // switching that kind back on.
            swapped.Clear();
            swappedFor = fingerprint;
        }
        // What the pile is showing right now, before this click is taken into account.
        // The next kind has to be measured from this, not from the page's own list:
        // otherwise a second click on the same rung asks for the same replacement and
        // the card looks dead.
        List<string> shown = new List<string>(kinds);
        for (int slot = 0; slot < shown.Count; slot++)
        {
            string stand;
            if (swapped.TryGetValue(slot, out stand)) shown[slot] = stand;
        }
        int clicked = pendingSlot;
        pendingSlot = -1;
        if (clicked >= 0 && clicked < shown.Count && shown[clicked].Length != 0)
        {
            // A closed preview card is replaced, not removed: the page cannot raise it
            // again, so the rung moves on to another kind the page still has switched
            // on. Every click advances, which is what makes closing the same rung twice
            // visibly different.
            List<string> others = new List<string>();
            for (int i = 0; i < shown.Count; i++) if (i != clicked) others.Add(shown[i]);
            swapped[clicked] = NextKind(shown[clicked], on, others);
            shown[clicked] = swapped[clicked];
        }
        bool empty = live.Count == 0;
        Options head = OptionsFor(0, spec, shown, count);
        // Geometry, not text: a card whose words changed is remade on its own, and
        // rebuilding the neighbours for it would flash the whole stack on every
        // keystroke in the title box.
        bool structural = empty || !live[0].SameGeometry(head);

        List<Card> made = new List<Card>();
        for (int slot = 0; slot < count; slot++) made.Add(null);
        // Built back to front. A window shown later sits on top of one shown
        // earlier, so raising the deepest rung first and the front one last leaves
        // the stack in the order it is read, without any card having to be pushed
        // down after it has already appeared - which is what made the stack look
        // like it was sorting itself while it faded in.
        long epoch = DateTime.UtcNow.Ticks;
        for (int slot = count - 1; slot >= 0; slot--)
        {
            Options pen = OptionsFor(slot, spec, shown, count);
            pen.DeckFront = slot == 0;
            Card existing = slot < live.Count ? live[slot] : null;
            if (existing != null && !structural && existing.SameLayout(pen))
            {
                existing.Restyle(pen);
                // The type dial moves the glyphs inside a card that is already
                // there, so it is applied in place rather than raised anew: a
                // rebuild per notch is what made the slider feel like it was
                // fighting the card instead of resizing it.
                existing.ReType(pen.FontSize);
                made[slot] = existing;
                continue;
            }
            // A card added to a stack that did not otherwise move is the one case
            // worth animating: sliding it out from behind is what shows where it
            // came from. A stack rebuilt because its geometry changed arrives
            // whole, and animating that would be a light show on every keystroke.
            pen.Instant = !empty && structural;
            pen.EntranceEpoch = epoch;
            made[slot] = Raise(pen);
        }
        // A rung that failed to come up is simply not part of the stack.
        made.RemoveAll(delegate(Card c) { return c == null; });
        // Finish the order for the rungs that were kept rather than raised: they
        // are still where the previous stack put them, so a fresh card behind a
        // reused front one would sit on top of it. made is in slot order, so slot 0
        // is the front of the deck.
        for (int slot = made.Count - 1; slot >= 1; slot--) made[slot].TuckBehind(made[slot - 1].Handle);

        List<Card> previous = live;
        live = made;
        for (int i = 0; i < previous.Count; i++)
        {
            // A card can be in both lists. Retiring it there would dispose it out
            // from under the stack that is still using it.
            if (made.IndexOf(previous[i]) < 0) Retire(previous[i]);
        }
    }

    // The kinds in the order the page lists them, so "the next one" is the next
    // switch on the page rather than an ordering invented here.
    static readonly string[] KindOrder = { "turnEnd", "approval", "question" };

    static List<string> PreviewKinds(Dictionary<string, string> spec)
    {
        List<string> kinds = new List<string>();
        string raw;
        if (!spec.TryGetValue("kinds", out raw) || raw.Length == 0) return kinds;
        string[] parts = raw.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string kind = parts[i].Trim();
            if (kind.Length != 0) kinds.Add(kind);
        }
        return kinds;
    }

    // The kind a closed rung takes next. It walks the ring from the current kind and
    // stays among the kinds the page still has switched on, so closing a card never
    // resurrects an event the reader turned off. Among those it prefers one the rest of
    // the pile is not showing - two identical cards side by side reads as a bug - and
    // when there is no such kind it just advances, because a rung closed twice has to
    // look like it changed twice.
    static string NextKind(string current, List<string> on, List<string> others)
    {
        int at = Array.IndexOf(KindOrder, current);
        if (at < 0) at = 0;
        string spare = null;
        for (int step = 1; step <= KindOrder.Length; step++)
        {
            string candidate = KindOrder[(at + step) % KindOrder.Length];
            if (candidate == current) continue;
            if (!on.Contains(candidate)) continue;
            if (!others.Contains(candidate)) return candidate;
            if (spare == null) spare = candidate;
        }
        return spare != null ? spare : current;
    }

    // A card shows one event kind, so its words come from that kind's own fields.
    // An empty kind is the blank card: the shape of a notice with nothing in it,
    // named by its position so three blanks are still three different cards.
    void ApplyKind(Options o, Dictionary<string, string> spec, string kind, int slot, int count)
    {
        o.PreviewKind = kind;
        string prefix = kind.Length == 0 ? "turnEnd" : kind;
        string v;
        o.Blank = kind.Length == 0;
        o.Title = spec.TryGetValue(prefix + "Title", out v) ? v : o.Title;
        o.Body = spec.TryGetValue(prefix + "Body", out v) ? v : o.Body;
        if (o.Blank)
        {
            // Numbered from the front of the pile: slot 0 is the card against the
            // corner, the one that is fully unfolded and that the later cards stack
            // on top of. It is also the first one to arrive, so it carries the
            // earliest stamp - the numbers and the clocks run the same way up the
            // stack instead of against each other.
            o.Title = BlankLabel(o.Language, slot);
            o.Body = "";
        }
        // One minute per rung, so a stack raised in a single instant still reads as
        // a pile of notices that arrived one after another. The front card arrived
        // first, which is why it takes the largest offset back from now.
        o.ClockMinutes = slot - (count - 1);
    }

    // "第一张", "第二张" ... in the reading language. Only a blank preview card
    // ever asks for one, and only so three blanks are still three different cards.
    static string BlankLabel(string language, int slot)
    {
        int n = slot + 1;
        string[] zh = { "第一张", "第二张", "第三张", "第四张", "第五张", "第六张", "第七张", "第八张", "第九张", "第十张", "第十一张", "第十二张" };
        string name = language == "zh" || language == "en"
            ? language
            : CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        if (name.StartsWith("zh") && n <= zh.Length) return zh[n - 1];
        return "Card " + n.ToString(CultureInfo.InvariantCulture);
    }

    Options OptionsFor(int slot, Dictionary<string, string> spec, List<string> kinds, int count)
    {
        Options o = baseOptions.Clone();
        o.Preview = true;
        o.Instant = true;
        o.PreviewSlot = slot;
        o.PreviewStamp = hostStamp;
        Apply(o, spec);
        string kind = kinds.Count == 0 ? "" : kinds[Math.Min(slot, kinds.Count - 1)];
        ApplyKind(o, spec, kind, slot, count);
        o.OnPreviewDismiss = HandleDismiss;
        return o;
    }

    // A click on a preview card does not close it: the page owns the stack, so the
    // card reports its slot and the stack puts the next kind there. Re-reading the
    // same spec is enough - Sync takes the slot from pendingSlot.
    void HandleDismiss(int slot)
    {
        pendingSlot = slot;
        dirty = true;
    }

    static int ParseCount(Dictionary<string, string> spec)
    {
        string stack;
        if (!spec.TryGetValue("maxStack", out stack)) return 1;
        int n;
        if (!int.TryParse(stack, out n) || n <= 0) return 1;
        return Math.Min(MaxCards, n);
    }

    static Card Raise(Options want)
    {
        try
        {
            Card card = new Card(want);
            card.Show();
            return card;
        }
        catch { return null; }
    }

    // Close, then dispose. Disposing a form outright skips OnFormClosed, and that
    // is where a card deletes its deck file: dispose alone would leave one stale
    // .card behind per rebuild, and a dragged slider rebuilds a lot.
    static void Retire(List<Card> cards)
    {
        for (int i = 0; i < cards.Count; i++) Retire(cards[i]);
    }
    static void Retire(Card card)
    {
        try { card.Close(); } catch { }
        try { card.Dispose(); } catch { }
    }

    static void Apply(Options o, Dictionary<string, string> spec)
    {
        string v;
        if (spec.TryGetValue("title", out v)) o.Title = v;
        if (spec.TryGetValue("body", out v)) o.Body = v;
        if (spec.TryGetValue("corner", out v)) o.Corner = v;
        if (spec.TryGetValue("fontSize", out v))
        {
            int n;
            if (int.TryParse(v, out n)) o.FontSize = n;
        }
        if (spec.TryGetValue("theme", out v)) o.Theme = v;
        if (spec.TryGetValue("animations", out v)) o.Animations = v;
        if (spec.TryGetValue("scale", out v)) o.Scale = v;
        if (spec.TryGetValue("language", out v)) o.Language = v;
        if (spec.TryGetValue("contrast", out v)) o.Contrast = v;
        if (spec.TryGetValue("maxStack", out v))
        {
            int n;
            if (int.TryParse(v, out n)) o.MaxStack = n;
        }
    }

    void Shutdown()
    {
        try { poll.Stop(); poll.Dispose(); } catch { }
        Retire(live);
        live.Clear();
        // Release the claim, but only if it is still this host's: a host that was
        // superseded must not delete the claim its successor just wrote.
        try
        {
            if (File.ReadAllText(claimPath).Trim() == hostStamp) File.Delete(claimPath);
        }
        catch { }
        ExitThread();
    }

    // One field per line, `key=value`, with backslash escapes so the body can
    // carry its line breaks. Chosen over JSON because the reader is a card
    // process with no parser and no tolerance for a half-written file.
    static Dictionary<string, string> ParseSpec(string text)
    {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text == null) return map;
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
        }
        return map;
    }

    static string Unescape(string s)
    {
        StringBuilder b = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                if (n == 'n') b.Append('\n');
                else if (n == 't') b.Append('\t');
                else b.Append(n);
            }
            else b.Append(c);
        }
        return b.ToString();
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
        bool passive = opt.Preview || opt.PreviewSpec.Length != 0;
        while (!passive && Native.ExclusiveFullScreen() && DateTime.UtcNow < deadline)
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
        // the conversation waiting on a human, which is not an interruption. A
        // preview is not one either: the reader asked for it by opening the
        // settings page.
        if (opt.DismissFile.Length == 0 && !passive && Native.QuietTime())
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

        // The preview host is a long-lived process rather than a card: it owns
        // the whole stack and rebuilds it in place. Starting it costs the same
        // ~300ms as any other card, which is why it must be the only thing the
        // settings page ever starts.
        if (opt.PreviewSpec.Length != 0)
        {
            Application.Run(new PreviewHost(opt));
            Native.timeEndPeriod(1);
            return 0;
        }

        Card card = new Card(opt);
        // Armed before the window is shown, and off the message loop, so it
        // survives the cases that matter: a Shown handler that never finishes and
        // a pump that never runs. Those are exactly what leaves a card up and
        // invisible for good. The slot it aims at is the one the entrance
        // animation lands on, so that is where a rescue puts it back.
        // A card that waits for an answer, and a preview, have no deadline of
        // their own: the first ends when the request settles, the second when the
        // settings page replaces it. Only a turn-end notice expires.
        int hardMs = opt.DismissFile.Length != 0 || opt.Preview ? 0 : Math.Max(1, opt.Seconds) * 1000 + 3000;
        Watchdog.Arm(card.Handle, card.SlotX, card.SlotYTarget, 1000, hardMs);

        Application.Run(card);

        Native.timeEndPeriod(1);
        return 0;
    }
}


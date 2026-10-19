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
using System.Drawing.Text;
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
    readonly Font captionFont;
    readonly Font bodyFont;
    readonly Color titleColor = Color.FromArgb(0x1B, 0x1B, 0x1F);
    readonly Color bodyColor = Color.FromArgb(0x44, 0x44, 0x4A);

    readonly string cardDir;
    readonly string cardFile;
    IntPtr prevHwnd = IntPtr.Zero;

    readonly int targetX, targetY, slotBase, slotTop, slotOffset;
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

    public Card(Options options)
    {
        opt = options;

        // Per-monitor V2, so GDI and ClearType render at the real dpi instead of
        // the shell stretching a 96dpi bitmap on a 125% display.
        using (Graphics probe = Graphics.FromHwnd(IntPtr.Zero)) scale = probe.DpiX / 96f;

        string ui = "Segoe UI";
        foreach (FontFamily f in FontFamily.Families)
            if (f.Name == "Segoe UI Variable Text") { ui = f.Name; break; }

        // Whole device pixels, not points: 11pt at 120dpi is 18.33px and that
        // fractional em size lands glyph stems off the pixel grid.
        captionFont = new Font(ui, (float)Math.Round(15 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        bodyFont = new Font(ui, (float)Math.Round(15 * scale), FontStyle.Regular, GraphicsUnit.Pixel);

        AutoScaleMode = AutoScaleMode.None;
        Text = opt.Title;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = SystemColors.Window;
        Size = new Size(Px(420), Px(160));
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        targetX = area.Right - Width;
        // Cards overlap instead of tiling: every card except the front one keeps
        // only this strip visible above the card in front of it. It is tall
        // enough to hold that card's title row and both its buttons, so the deck
        // can still be cleared one card at a time, front to back.
        slotOffset = Px(44);
        slotBase = area.Bottom - Height;
        slotTop = area.Top + Px(8);

        cardDir = Path.Combine(Path.GetTempPath(), "dsh-notice-cards");
        try { Directory.CreateDirectory(cardDir); } catch { }
        cardFile = Path.Combine(cardDir, string.Format("{0:D19}-{1}.card", DateTime.Now.Ticks, Process.GetCurrentProcess().Id));

        targetY = SlotY();
        Location = new Point(targetX, targetY + Px(28));
        Opacity = 0;

        // Rounded corners: a Region clips everything drawn, including the border,
        // so the shape stays consistent at every size. 8px is the toast radius.
        int radius = Px(8);
        shape = new GraphicsPath();
        shape.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
        shape.AddArc(Width - radius * 2, 0, radius * 2, radius * 2, 270, 90);
        shape.AddArc(Width - radius * 2, Height - radius * 2, radius * 2, radius * 2, 0, 90);
        shape.AddArc(0, Height - radius * 2, radius * 2, radius * 2, 90, 90);
        shape.CloseAllFigures();
        Region = new Region(shape);

        // Both text blocks are hand-painted instead of living in a Label: GDI
        // ClearType puts a coloured sub-pixel fringe on the glyphs, which on a
        // white card at 125% is the streaky text this used to show. GDI+ with
        // AntiAliasGridFit is greyscale.
        using (Graphics measure = Graphics.FromHwnd(IntPtr.Zero))
        {
            bodyWidth = Px(372);
            bodyHeight = Px(92);
            barWidth = Px(8);
            lineHeight = (int)Math.Ceiling(bodyFont.GetHeight(measure));
            bodyLines = SplitLines(opt.Body, measure, bodyFont, bodyWidth);
        }
        int totalHeight = bodyLines.Count * lineHeight;
        maxScroll = Math.Max(0, totalHeight - bodyHeight);
        thumbHeight = Math.Max(Px(20), (int)((long)bodyHeight * bodyHeight / Math.Max(1, totalHeight)));

        // The title sits in its own panel so a click anywhere on it still dismisses.
        BufferedPanel caption = new BufferedPanel();
        caption.Location = new Point(Px(20), Px(18));
        caption.Size = new Size(Px(320), Px(26));
        caption.BackColor = SystemColors.Window;
        caption.Paint += delegate(object s, PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (SolidBrush b = new SolidBrush(titleColor))
                e.Graphics.DrawString(opt.Title, captionFont, b, 0f, 0f);
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
        bodyBox.BackColor = SystemColors.Window;
        bodyBox.Paint += delegate(object s, PaintEventArgs e) { PaintBody(e.Graphics); };
        bodyBox.MouseWheel += delegate(object s, MouseEventArgs e)
        {
            // Delta is negative when the wheel rolls down, and rolling down moves
            // the text up, so the sign is flipped. Three rows per notch.
            double step = -e.Delta / 120.0 * lineHeight * 3;
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

        AddIconButton(420 - 64, DrawWindowGlyph, delegate { FocusApp(); BeginExit(); });
        AddIconButton(420 - 32, DrawCloseGlyph, delegate { BeginExit(); });

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
        using (Pen pen = new Pen(Color.FromArgb(0x1A, 0, 0, 0)))
            e.Graphics.DrawPath(pen, shape);
    }

    int Px(double v) { return (int)Math.Round(v * scale); }
    float Pf(double v) { return (float)Math.Round(v * scale); }

    // --- deck ---

    // Slot 0 is the oldest card alive and owns the front of the deck: the fully
    // drawn, bottom-right position. Every later card is tucked behind it and one
    // step higher. A file whose owning process is gone is swept here too, so the
    // stack cannot grow phantom slots.
    int SlotY()
    {
        List<string> live = new List<string>();
        string[] files;
        try { files = Directory.GetFiles(cardDir, "*.card"); }
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
        return (int)Math.Max(slotTop, slotBase - index * slotOffset);
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

    void PaintBody(Graphics g)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (SolidBrush brush = new SolidBrush(bodyColor))
        {
            // Draw from the first line that is partly visible, offset by the
            // fractional remainder, so the text moves by whole pixels instead of
            // jumping a row.
            int first = Math.Max(0, (int)Math.Floor(scroll / lineHeight));
            double offset = scroll - first * lineHeight;
            int last = Math.Min(bodyLines.Count, first + (int)Math.Ceiling((double)bodyHeight / lineHeight) + 1);
            for (int i = first; i < last; i++)
            {
                float y = (float)((i - first) * lineHeight - offset);
                g.DrawString(bodyLines[i], bodyFont, brush, 0f, y);
            }
        }
        if (maxScroll > 0)
        {
            // A hairline track with a proportionally sized thumb, like a real bar.
            g.FillRectangle(SystemBrushes.ControlLight, new Rectangle(bodyWidth + Px(3), 0, Px(2), bodyHeight));
            int thumbY = (int)((bodyHeight - thumbHeight) * scroll / maxScroll);
            g.FillRectangle(SystemBrushes.ControlDark, new Rectangle(bodyWidth + Px(2), thumbY, Px(4), thumbHeight));
        }
    }

    void DrawWindowGlyph(Graphics g, int w, int h)
    {
        float cw = Pf(13), ch = Pf(11);
        float x = (w - cw) / 2, y = (h - ch) / 2;
        using (Pen pen = new Pen(SystemColors.WindowText, Pf(1.2)))
        {
            g.DrawRectangle(pen, x, y, cw, ch);
            float rule = (float)(y + ch * 0.3);
            g.DrawLine(pen, x, rule, x + cw, rule);
        }
    }

    void DrawCloseGlyph(Graphics g, int w, int h)
    {
        float s = Pf(5), cx = w / 2f, cy = h / 2f;
        using (Pen pen = new Pen(SystemColors.WindowText, Pf(1.2)))
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
    void AddIconButton(int left, Action<Graphics, int, int> draw, EventHandler onClick)
    {
        BufferedPanel p = new BufferedPanel();
        p.Location = new Point(Px(left), Px(12));
        p.Size = new Size(Px(28), Px(28));
        p.BackColor = SystemColors.Window;
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
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(0x12, 0, 0, 0)))
                        e.Graphics.FillPath(fill, hp);
                }
            }
            draw(e.Graphics, p.Width, p.Height);
        };
        p.MouseEnter += delegate { hover = true; p.Invalidate(); };
        p.MouseLeave += delegate { hover = false; p.Invalidate(); };
        p.Click += onClick;
        Controls.Add(p);
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
    static List<string> SplitLines(string text, Graphics g, Font font, float width)
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
                if (g.MeasureString(candidate, font).Width <= width) { current = candidate; continue; }
                if (current.Length != 0) { lines.Add(current); current = ""; }
                if (g.MeasureString(word, font).Width <= width) { current = word; continue; }
                string chunk = "";
                foreach (char ch in word)
                {
                    string grown = chunk + ch;
                    if (chunk.Length != 0 && g.MeasureString(grown, font).Width > width)
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

    // Slide up and fade in, ease-out cubic so it settles instead of stopping
    // dead, with a small overshoot (ease-out back) so the card reads as landing
    // rather than being dragged into place.
    void EntranceFrame()
    {
        double t = Math.Min(1.0, entranceSw.Elapsed.TotalMilliseconds / 420.0);
        double ease = 1 - Math.Pow(1 - t, 3);
        double c1 = 1.70158, c3 = c1 + 1;
        double back = 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
        Opacity = ease;
        Location = new Point(targetX, (int)Math.Round(targetY + Px(36) * (1 - back)));
        if (t < 1.0) return;
        // WinForms rewrites the extended styles while animating Opacity; put
        // NOACTIVATE back (and keep TOOLWINDOW off) so the card keeps ignoring
        // focus but still accepts clicks for the rest of its life.
        int ex = Native.GetWindowLong(Handle, -20);
        ex &= ~0x00000080;
        Native.SetWindowLong(Handle, -20, ex | 0x08000000);
        settled = true;
        if (!gliding) { StopPump(); return; }
    }

    // Exit: fade out while sinking back toward the taskbar, ease-in cubic so it
    // leaves slowly and then accelerates away. Every close path funnels here.
    void ExitFrame()
    {
        double t = Math.Min(1.0, exitSw.Elapsed.TotalMilliseconds / 220.0);
        double ease = Math.Pow(t, 3);
        Opacity = exitFromOpacity * (1 - ease);
        Location = new Point(targetX, (int)Math.Round(exitFromY + Px(24) * ease));
        if (t >= 1.0)
        {
            StopPump();
            Close();
        }
    }

    void GlideFrame()
    {
        double delta = scrollTarget - scroll;
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

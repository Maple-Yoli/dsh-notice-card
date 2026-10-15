# Alerts on a DSH turn stop or a DSH wait (approval / question): a notice card
# modelled on the Windows toast (system colors, Segoe UI, thin border, small
# corner buttons), pinned to the bottom-right corner and sliding up as it fades
# in. It sits above other windows but never takes focus, so it never interrupts
# typing or a full-screen video, and it does not depend on Windows notifications
# being switched on.
# Windows only: it needs Windows PowerShell 5.1, which ships with Windows.
# The file keeps a UTF-8 BOM so 5.1 parses the Chinese literals correctly.
param(
    [string] $Title   = 'DeepSeek Harness',
    [string] $Body    = '任务已结束，等你下一步。',
    [int]    $Seconds = 10,
    [string] $DismissFile = '',
    # The window button focuses the DSH window; set AppPath to the executable to
    # launch when DSH is not running. Both are supplied by the plugin (which
    # knows where DSH lives), so nothing machine-specific is hardcoded here.
    [string] $ProcessName = 'DeepSeek Harness',
    [string] $AppPath = '',
    [switch] $Popup
)

$exe = "$($env:SystemRoot)\System32\WindowsPowerShell\v1.0\powershell.exe"

if (-not $Popup) {
    # Show the card from a detached child so the caller (a hook command, or the
    # approval/request listener) never blocks on the message loop.
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-Popup', '-Title', "`"$Title`"", '-Body', "`"$Body`"", '-Seconds', "$Seconds")
    if ($DismissFile) { $args += @('-DismissFile', "`"$DismissFile`"") }
    if ($ProcessName) { $args += @('-ProcessName', "`"$ProcessName`"") }
    if ($AppPath) { $args += @('-AppPath', "`"$AppPath`"") }
    Start-Process -FilePath $exe -WindowStyle Hidden -ArgumentList $args
    exit 0
}

$ErrorActionPreference = 'Stop'

# --- the card ---
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
# The interop shim is compiled once into a DLL and merely loaded after that.
# Add-Type -TypeDefinition runs the C# compiler on every card (~100ms), which is
# a visible slice of the gap between the in-app prompt and this card. The DLL
# lives under %LOCALAPPDATA% rather than next to this script, because the script
# is installed inside a node_modules tree that may be read-only. Delete the DLL
# whenever the C# below changes, so it is rebuilt.
$notifyDll = Join-Path (Join-Path $env:LOCALAPPDATA 'dsh-notice-card') 'NotifyWin.dll'
$notifySrc = @'
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
public class NotifyWin : IMessageFilter {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(int value);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    // WinForms' own Timer rides WM_TIMER, which the OS coalesces to the 15.6ms
    // system tick: a 8ms Interval really paints at ~55fps with jitter up to
    // 20ms, so an entrance animation lands in a handful of uneven steps. This
    // pump posts a private message from a background thread instead, which the
    // modal loop dispatches through the filter below at whatever period it is
    // given. It runs only while something is actually animating.
    public const int WM_FRAME = 0x8000 + 1;
    public static Action Frame;
    static Thread pump;
    static volatile bool running;
    static IntPtr target;
    static NotifyWin filter;
    static NotifyWin() { filter = new NotifyWin(); Application.AddMessageFilter(filter); }
    public static void StartPump(IntPtr h, int ms) {
        StopPump();
        target = h;
        running = true;
        Thread t = new Thread(delegate() {
            while (running) {
                if (!PostMessage(target, WM_FRAME, IntPtr.Zero, IntPtr.Zero)) break;
                Thread.Sleep(ms);
            }
        });
        t.IsBackground = true;
        pump = t;
        t.Start();
    }
    public static void StopPump() {
        running = false;
        Thread t = pump;
        pump = null;
        if (t != null) { try { t.Join(200); } catch {} }
    }
    public bool PreFilterMessage(ref Message m) {
        if (m.Msg != WM_FRAME) return false;
        Action f = Frame;
        if (f != null) { try { f(); } catch {} }
        return true;
    }
}
'@
if (-not (Test-Path -LiteralPath $notifyDll)) {
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $notifyDll) -Force
    # -OutputAssembly only writes the DLL, it does not load it, so the Add-Type
    # -Path below is what actually makes the type available on this first run.
    Add-Type -TypeDefinition $notifySrc -OutputAssembly $notifyDll -ReferencedAssemblies System.Windows.Forms
}
Add-Type -Path $notifyDll

# Without this the process stays system-DPI aware: on a 125% display the form is
# laid out at 96dpi and the shell stretches the bitmap, which is exactly the
# blurry rendering this card had. Per-monitor V2 makes GDI and ClearType render
# at the real 120dpi. Must run before any window is created.
$null = [NotifyWin]::SetProcessDpiAwarenessContext(-4)

# WinForms timers ride the system timer tick, which defaults to ~15.6ms no
# matter what Interval says, so a 16ms animation really only repaints 3-4 times
# a second's worth of motion. Raising the global timer resolution to 1ms is
# what lets the entrance animation actually hit its interval. Released at the
# end of the script so the card does not tax the whole desktop while it lives.
$null = [NotifyWin]::timeBeginPeriod(1)

# Cards form a deck instead of replacing each other. Every live card drops one
# file named by its creation stamp into a shared directory and re-reads that
# directory a few times a second; sorting by name recovers the order the cards
# appeared in, and that index picks the vertical slot. The oldest card takes the
# bottom-right corner fully drawn and is held in front, because it is the one to
# deal with first; every later card is tucked behind it and one step higher, so
# only its top strip (title row and its two buttons) shows. A file whose owning
# process is gone (a crash, or a killed card) is swept here too, so the stack
# cannot grow phantom slots.
$cardDir = Join-Path ([System.IO.Path]::GetTempPath()) 'dsh-notice-cards'
$null = New-Item -ItemType Directory -Path $cardDir -Force
$cardFile = Join-Path $cardDir ('{0:D19}-{1}.card' -f (Get-Date).Ticks, $PID)

# Remember who had focus: the card is shown with SWP_NOACTIVATE and focus is
# handed straight back, so this stays a notice and not a dialog.
$previous = [NotifyWin]::GetForegroundWindow()

# Segoe UI Variable Text is the Windows 11 shell face; fall back to Segoe UI so
# the card matches whichever shell this is running on.
$families = [System.Drawing.FontFamily]::Families.Name
$ui = if ($families -contains 'Segoe UI Variable Text') { 'Segoe UI Variable Text' } else { 'Segoe UI' }

# The fonts are sized in whole device pixels, not points. 11pt at 120dpi is
# 18.33px, and that fractional em size lands glyph stems off the pixel grid, so
# the text looks broken up; a whole-pixel size is grid-fit and renders solid.
$scale = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero).DpiX / 96
function px { param([double] $v) [int][Math]::Round($v * $scale) }
function fp { param([double] $v) [single][Math]::Round($v * $scale) }
function pf { param([double] $v) [single]($v * $scale) }

# A hand-painted panel erases its background and only then draws, so every
# repaint shows a white flash between the two — on a scroll that is one flash
# per wheel tick, which is the flicker. Double buffering collapses erase and
# paint into a single blit. The property is protected on Control, so it has to
# be reached through reflection.
function Set-DoubleBuffered {
    param([System.Windows.Forms.Control] $control)
    $prop = [System.Windows.Forms.Control].GetProperty('DoubleBuffered', [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::NonPublic)
    if ($prop) { $prop.SetValue($control, $true) }
}

# Both text blocks are painted by hand instead of living in a Label/RichTextBox.
# GDI ClearType puts a coloured sub-pixel fringe on the glyphs, which on a white
# card at 125% is exactly the streaky, broken-looking text that was reported;
# neither lfQuality on an HFONT nor UseCompatibleTextRendering changes that,
# because those controls rebuild their own font and always ask GDI for ClearType.
# Drawing with GDI+ and TextRenderingHint = AntiAliasGridFit is greyscale and
# measures 0% coloured pixels against 84% for the control path.
$captionFont = [System.Drawing.Font]::new($ui, [single](fp 15), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$bodyFont = [System.Drawing.Font]::new($ui, [single](fp 15), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
# Not pure black: the shell's own toasts use a near-black title over a softer
# body, which reads calmer against white than WindowText does.
$titleColor = [System.Drawing.Color]::FromArgb(0x1B, 0x1B, 0x1F)
$bodyColor = [System.Drawing.Color]::FromArgb(0x44, 0x44, 0x4A)

# Word wrap and clipping for the hand-painted body. A plain Graphics.DrawString
# ignores the layout rectangle's right edge, so break the text into lines first
# and let the caller stop at the first line that would overflow the box.
#
# Chinese has no spaces, so splitting on spaces yields one enormous "word" and
# nothing ever wraps: the whole paragraph ran off the right edge and got cut
# mid-glyph. A token that cannot fit on a line of its own is therefore broken
# down character by character, which is also the correct rule for CJK text.
#
# Characters that may not open a line (kinsoku shori): a closing mark left at
# the start of the next line reads as a typo, so it stays on the line it closes
# even if that line then runs a hair past the measure.
function Split-Lines {
    param([string] $text, [System.Drawing.Graphics] $g, [System.Drawing.Font] $font, [double] $width)
    # Kept inside the function: the wrap helper is self-contained, so extracting
    # or dot-sourcing it cannot leave the set behind and break every line break.
    $noLineStart = '。，、；：？！）】》」』〉·％,.;:?!)]}'
    $lines = New-Object System.Collections.ArrayList
    foreach ($paragraph in ($text -split "`r?`n")) {
        if ($paragraph -eq '') { $null = $lines.Add(''); continue }
        $current = ''
        foreach ($word in ($paragraph -split ' ')) {
            $candidate = if ($current -eq '') { $word } else { "$current $word" }
            if ($g.MeasureString($candidate, $font).Width -le $width) { $current = $candidate; continue }
            if ($current -ne '') {
                $null = $lines.Add($current)
                $current = ''
            }
            if ($g.MeasureString($word, $font).Width -le $width) { $current = $word; continue }
            $chunk = ''
            foreach ($ch in $word.ToCharArray()) {
                $grown = "$chunk$ch"
                if ($chunk -ne '' -and $g.MeasureString($grown, $font).Width -gt $width) {
                    if ($noLineStart.Contains([string]$ch)) {
                        $null = $lines.Add($grown)
                        $chunk = ''
                    } else {
                        $null = $lines.Add($chunk)
                        $chunk = [string]$ch
                    }
                } else {
                    $chunk = $grown
                }
            }
            $current = $chunk
        }
        if ($current -ne '') { $null = $lines.Add($current) }
    }
    $lines
}

$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.Size = [System.Drawing.Size]::new((px 420), (px 160))
$form.BackColor = [System.Drawing.SystemColors]::Window
$form.ShowInTaskbar = $false
$form.TopMost = $true
Set-DoubleBuffered $form

# Flush to both edges: WorkingArea already excludes the taskbar, so its
# bottom-right corner is exactly "screen edge plus top of the taskbar".
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$targetX = $area.Right - $form.Width
# Cards overlap instead of tiling: every card except the front one keeps only
# this strip of itself visible above the card in front of it. It is tall enough
# to hold that card's title row and both its buttons, so the deck can still be
# cleared one card at a time, front to back.
$slotOffset = px 44
$slotBase = $area.Bottom - $form.Height
$slotTop = $area.Top + (px 8)

# Slot 0 is the oldest card alive and owns the front of the deck: the fully
# drawn, bottom-right position. Every later card is tucked behind it and one
# step higher, so only that card's top strip (title row and its two buttons)
# stays visible. The card that must be dealt with first is therefore both the
# one in front and the one that is completely readable.
function Get-SlotY {
    $live = New-Object System.Collections.ArrayList
    foreach ($f in [System.IO.Directory]::GetFiles($cardDir, '*.card')) {
        $parts = ([System.IO.Path]::GetFileNameWithoutExtension($f)) -split '-'
        $owner = 0
        if ($parts.Length -ge 2) { $null = [int]::TryParse($parts[1], [ref]$owner) }
        if ($owner -ne 0 -and $owner -ne $PID -and -not (Get-Process -Id $owner -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
            continue
        }
        $null = $live.Add($f)
    }
    $sorted = @($live | Sort-Object)
    $idx = 0
    for ($i = 0; $i -lt $sorted.Count; $i++) { if ($sorted[$i] -eq $cardFile) { $idx = $i; break } }
    $script:prevHwnd = [IntPtr]::Zero
    if ($idx -gt 0) {
        $raw = ''
        try { $raw = [System.IO.File]::ReadAllText($sorted[$idx - 1]) } catch { }
        $prev = [long]0
        if ([long]::TryParse($raw.Trim(), [ref]$prev) -and $prev -ne 0) { $script:prevHwnd = [IntPtr]::new($prev) }
    }
    $y = $slotBase - $idx * $slotOffset
    [int][Math]::Max($slotTop, $y)
}

$script:prevHwnd = [IntPtr]::Zero
$targetY = Get-SlotY
# Use ::new(), not New-Object: in argument mode `Point($a, $b + 28)` splits into
# three arguments and the constructor lookup fails.
$form.Location = [System.Drawing.Point]::new($targetX, $targetY + (px 28))
$form.Opacity = 0

# Rounded corners: a Region clips everything drawn, including the border, so the
# shape stays consistent at every size. 8px is the Windows 11 toast radius.
$radius = px 8
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $radius * 2, $radius * 2, 180, 90)
$path.AddArc($form.Width - $radius * 2, 0, $radius * 2, $radius * 2, 270, 90)
$path.AddArc($form.Width - $radius * 2, $form.Height - $radius * 2, $radius * 2, $radius * 2, 0, 90)
$path.AddArc(0, $form.Height - $radius * 2, $radius * 2, $radius * 2, 90, 90)
$path.CloseAllFigures()
$form.Region = New-Object System.Drawing.Region($path)

$form.Add_Paint({
    param($sender, $e)
    $e.Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    # Hairline border only; the shell draws no accent stripe on its toasts.
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0x1A, 0x00, 0x00, 0x00))
    $e.Graphics.DrawPath($pen, $path)
    $pen.Dispose()
})

$script:closed = $false
# Closing runs the exit animation instead of dropping the window outright, so
# every path out (the X, the life timer, the dismiss file, the title row) goes
# through the same fade. $beginExit is defined further down, once the timers it
# has to stop exist; a scriptblock body resolves its variables when it runs.
$close = { if (-not $script:closed) { $script:closed = $true; & $beginExit } }

# Both text blocks are hand-painted Panels. Drawing with GDI+ and
# TextRenderingHint = AntiAliasGridFit is greyscale (0% coloured pixels measured
# against 84% for the Label/RichTextBox path), which is what kills the streaky
# ClearType fringe on a white card at 125%.
$measure = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero)

# The title sits in its own panel so a click anywhere on it still dismisses.
$caption = New-Object System.Windows.Forms.Panel
$caption.Location = [System.Drawing.Point]::new((px 20), (px 18))
$caption.Size = [System.Drawing.Size]::new((px 320), (px 26))
$caption.BackColor = [System.Drawing.SystemColors]::Window
Set-DoubleBuffered $caption
$caption.Add_Paint({ param($s, $e)
    $e.Graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $brush = New-Object System.Drawing.SolidBrush($titleColor)
    $e.Graphics.DrawString($Title, $captionFont, $brush, [single]0, [single]0)
    $brush.Dispose()
})
$caption.Add_Click($close)
$form.Controls.Add($caption)

# The body scrolls instead of running off the bottom. WinForms controls cannot
# be scrolled without keyboard focus and this card deliberately never takes
# focus, so the offset is ours: the wheel adjusts it, and the thumb can be
# dragged, which works because plain mouse messages do reach the card.
$bodyLeft = px 20
$bodyTop = px 48
$bodyWidth = px 372
$bodyHeight = px 92
$barWidth = px 8
# The offset is kept in pixels, not line numbers: a line-quantised offset can
# only ever move a whole row at a time, which is what made the wheel and the
# thumb look like they were advancing one frame at a time. Pixels let the text
# rest anywhere, so a drag tracks the cursor exactly and the wheel can glide.
$lineHeight = [int][Math]::Ceiling($bodyFont.GetHeight($measure))
$bodyLines = @(Split-Lines $Body $measure $bodyFont $bodyWidth)
$totalHeight = $bodyLines.Count * $lineHeight
$maxScroll = [Math]::Max(0, $totalHeight - $bodyHeight)
$thumbHeight = [Math]::Max((px 20), [int]($bodyHeight * $bodyHeight / [Math]::Max(1, $totalHeight)))
$script:scroll = 0.0
$script:scrollTarget = 0.0
$script:dragging = $false
$script:dragGrab = 0.0
$script:gliding = $false

$bodyBox = New-Object System.Windows.Forms.Panel
$bodyBox.Location = [System.Drawing.Point]::new($bodyLeft, $bodyTop)
$bodyBox.Size = [System.Drawing.Size]::new(($bodyWidth + $barWidth), $bodyHeight)
$bodyBox.BackColor = [System.Drawing.SystemColors]::Window
Set-DoubleBuffered $bodyBox
$bodyBox.Add_Paint({ param($s, $e)
    $g = $e.Graphics
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $brush = New-Object System.Drawing.SolidBrush($bodyColor)
    # Draw from the first line that is partly visible, offset by the fractional
    # remainder, so the text moves by whole pixels instead of jumping a row.
    $first = [Math]::Max(0, [int][Math]::Floor($script:scroll / $lineHeight))
    $offset = $script:scroll - ($first * $lineHeight)
    $last = [Math]::Min($bodyLines.Count, $first + [int][Math]::Ceiling($bodyHeight / $lineHeight) + 1)
    for ($i = $first; $i -lt $last; $i++) {
        $y = [single]((($i - $first) * $lineHeight) - $offset)
        $g.DrawString($bodyLines[$i], $bodyFont, $brush, [single]0, $y)
    }
    $brush.Dispose()
    if ($maxScroll -gt 0) {
        # A hairline track with a proportionally sized thumb, like a real bar.
        $track = [System.Drawing.Rectangle]::new(($bodyWidth + (px 3)), 0, (px 2), $bodyHeight)
        $g.FillRectangle([System.Drawing.SystemBrushes]::ControlLight, $track)
        $thumbY = [int](($bodyHeight - $thumbHeight) * $script:scroll / $maxScroll)
        $g.FillRectangle([System.Drawing.SystemBrushes]::ControlDark, [System.Drawing.Rectangle]::new(($bodyWidth + (px 2)), $thumbY, (px 4), $thumbHeight))
    }
})
# The wheel only arrives here because Windows forwards it to the window under
# the cursor ("scroll inactive windows"), which is the whole reason this works
# on a card that never takes focus.
$bodyBox.Add_MouseWheel({ param($s, $e)
    # Delta is negative when the wheel rolls down, and rolling down moves the
    # text up, so the sign is flipped. Three rows per notch.
    $step = -$e.Delta / 120.0 * $lineHeight * 3
    $script:scrollTarget = [Math]::Max(0.0, [Math]::Min([double]$maxScroll, $script:scrollTarget + $step))
    $script:gliding = $true
    # The pump stops itself once the entrance animation and any glide are done,
    # so a later wheel has to bring it back for the easing frames.
    if ($script:settled) { [NotifyWin]::StartPump($form.Handle, 4) }
})
$bodyBox.Add_MouseDown({ param($s, $e)
    if ($e.X -ge $bodyWidth -and $maxScroll -gt 0) {
        $script:dragging = $true
        # Grab the thumb where it was clicked, so it does not jump under the
        # cursor on the first move.
        $thumbY = ($bodyHeight - $thumbHeight) * $script:scroll / $maxScroll
        $script:dragGrab = $e.Y - $thumbY
    }
})
$bodyBox.Add_MouseUp({ $script:dragging = $false })
$bodyBox.Add_MouseMove({ param($s, $e)
    if ($script:dragging -and $maxScroll -gt 0) {
        $travel = [Math]::Max(1, $bodyHeight - $thumbHeight)
        $script:scroll = [Math]::Max(0.0, [Math]::Min([double]$maxScroll, ($e.Y - $script:dragGrab) * $maxScroll / $travel))
        $script:scrollTarget = $script:scroll
        $bodyBox.Invalidate()
    }
})
$form.Controls.Add($bodyBox)
$measure.Dispose()

# The wheel sets a target and the shared pump frame eases toward it, which is
# what makes a notch look like a glide rather than a jump. The drag path
# bypasses it and writes $script:scroll directly, because a thumb under the
# cursor has to track it exactly. This used to be its own WinForms Timer, but
# that path is capped near 55fps by the 15.6ms system tick, so it rode the same
# ~200fps pump as the entrance animation instead.

# Two small icon buttons in the top-right corner. The glyphs are drawn with GDI+
# rather than taken from an icon font so nothing depends on which fonts are
# installed, and every coordinate goes through px/pf: the button is sized in
# scaled pixels, so an unscaled glyph would sit off-centre at anything but 100%.
function New-IconButton {
    param([int] $left, [scriptblock] $draw, [scriptblock] $onClick)
    $p = New-Object System.Windows.Forms.Panel
    $p.Location = [System.Drawing.Point]::new((px $left), (px 12))
    $p.Size = [System.Drawing.Size]::new((px 28), (px 28))
    $p.BackColor = [System.Drawing.SystemColors]::Window
    Set-DoubleBuffered $p
    $p.Cursor = [System.Windows.Forms.Cursors]::Hand
    $p.TabStop = $false
    # Hover state lives on the panel itself: both buttons come out of this same
    # function, and a shared script variable would light both of them up.
    $p.Tag = @{ hover = $false }
    # A scriptblock written inside this function does not see its locals when the
    # event fires later, so bind them now. Without GetNewClosure() the paint
    # handler cannot find $draw, every repaint raises, and WinForms answers with
    # a modal unhandled-exception dialog that blocks the message loop: the card
    # then never closes and the process never exits.
    $p.Add_Paint({ param($s, $e)
        $g = $e.Graphics
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        if ($p.Tag.hover) {
            # Rounded like the card itself; a square hover block would poke out
            # past the card's corner radius and read as a stray grey box.
            $r = px 5
            $hp = New-Object System.Drawing.Drawing2D.GraphicsPath
            $hp.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
            $hp.AddArc($p.Width - $r * 2, 0, $r * 2, $r * 2, 270, 90)
            $hp.AddArc($p.Width - $r * 2, $p.Height - $r * 2, $r * 2, $r * 2, 0, 90)
            $hp.AddArc(0, $p.Height - $r * 2, $r * 2, $r * 2, 90, 90)
            $hp.CloseAllFigures()
            $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x12, 0x00, 0x00, 0x00))
            $g.FillPath($fill, $hp)
            $fill.Dispose()
            $hp.Dispose()
        }
        & $draw $g $p.Width $p.Height
    }.GetNewClosure())
    $p.Add_MouseEnter({ $p.Tag.hover = $true; $p.Invalidate() }.GetNewClosure())
    $p.Add_MouseLeave({ $p.Tag.hover = $false; $p.Invalidate() }.GetNewClosure())
    $p.Add_Click($onClick)
    $form.Controls.Add($p)
}

# Window glyph: frame plus title-bar rule, centred on both axes.
$null = New-IconButton (420 - 64) {
    param($g, $w, $h)
    $cw = pf 13
    $ch = pf 11
    $x = [single](($w - $cw) / 2)
    $y = [single](($h - $ch) / 2)
    $pw = pf 1.2
    $pen = New-Object System.Drawing.Pen([System.Drawing.SystemColors]::WindowText, $pw)
    $g.DrawRectangle($pen, $x, $y, $cw, $ch)
    $rule = [single]($y + ($ch * 0.3))
    $g.DrawLine($pen, $x, $rule, [single]($x + $cw), $rule)
    $pen.Dispose()
} {
    # SW_RESTORE first: a minimized DSH has a live handle that SetForegroundWindow
    # alone will not bring back. Both the process name and the launch path come
    # from -ProcessName / -AppPath, so this stays machine-independent.
    $proc = if ($ProcessName) {
        Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    }
    if ($proc) {
        $null = [NotifyWin]::ShowWindow($proc.MainWindowHandle, 9)
        $null = [NotifyWin]::SetForegroundWindow($proc.MainWindowHandle)
    } elseif ($AppPath -and (Test-Path -LiteralPath $AppPath)) {
        Start-Process -FilePath $AppPath
    }
    & $close
}

# X glyph: two round-capped strokes, centred on both axes.
$null = New-IconButton (420 - 32) {
    param($g, $w, $h)
    $s = pf 5
    $cx = [single]($w / 2)
    $cy = [single]($h / 2)
    $pw = pf 1.2
    $pen = New-Object System.Drawing.Pen([System.Drawing.SystemColors]::WindowText, $pw)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, [single]($cx - $s), [single]($cy - $s), [single]($cx + $s), [single]($cy + $s))
    $g.DrawLine($pen, [single]($cx + $s), [single]($cy - $s), [single]($cx - $s), [single]($cy + $s))
    $pen.Dispose()
} $close

$form.Add_Shown({
    $h = $form.Handle
    # The card's own handle is what the deck ordering is anchored on, so it is
    # published as soon as it exists. Written here rather than before ShowDialog
    # because a handle only becomes valid once the window is created, and the
    # deck must never be pointed at a stale one.
    [System.IO.File]::WriteAllText($cardFile, $h.ToInt64().ToString())
    # Re-assert the styles on the real handle: TopMost alone can be beaten by a
    # shell-owned full-screen window, and WS_EX_NOACTIVATE is what stops
    # ShowDialog from pulling keyboard focus onto the card. WS_EX_TOOLWINDOW is
    # deliberately cleared: a tool window does not take part in normal mouse
    # hit-testing reliably, and with it set a real click on the allow button
    # landed on the card window itself instead of the button.
    $ex = [NotifyWin]::GetWindowLong($h, -20)
    $ex = $ex -band (-bnot 0x00000080)
    $null = [NotifyWin]::SetWindowLong($h, -20, $ex -bor 0x00000008 -bor 0x08000000)
    $null = [NotifyWin]::SetWindowPos($h, [IntPtr]::new(-1), 0, 0, 0, 0, 0x0010 -bor 0x0001 -bor 0x0002)
    if ($previous -ne [IntPtr]::Zero) { $null = [NotifyWin]::SetForegroundWindow($previous) }
    [NotifyWin]::StartPump($h, 4)
    $life.Start()
    $reflow.Start()
    if ($watch) { $watch.Start() }
})

# Slide up and fade in, ease-out cubic so it settles instead of stopping dead.
# The slide gets a small overshoot (ease-out back) so the card reads as landing
# rather than being dragged into place. Frames come from the pump in NotifyWin,
# not a WinForms Timer: at 4ms it paints around 200fps, which covers this
# display's 180Hz, so the motion is a continuous glide instead of the ~55fps
# stutter the system timer tick allowed.
$animMs = 420
$slideFrom = px 36
$sw = [System.Diagnostics.Stopwatch]::StartNew()
[NotifyWin]::Frame = [Action]{
    if ($script:exiting) {
        $t = [Math]::Min(1.0, $script:exitSw.Elapsed.TotalMilliseconds / $exitMs)
        # ease-in cubic: leaves slowly, then accelerates away, which reads as the
        # card being taken back rather than dropped.
        $ease = [Math]::Pow($t, 3)
        $form.Opacity = $script:exitFromOpacity * (1 - $ease)
        $form.Location = [System.Drawing.Point]::new($targetX, [int][Math]::Round($script:exitFromY + ($exitSlide * $ease)))
        if ($t -ge 1.0) {
            [NotifyWin]::StopPump()
            $form.Close()
        }
        return
    }
    if (-not $script:settled) {
        $t = [Math]::Min(1.0, $sw.Elapsed.TotalMilliseconds / $animMs)
        $ease = 1 - [Math]::Pow(1 - $t, 3)
        # ease-out back: overshoots by ~3% around t=0.7, then returns to exactly 1.
        $c1 = 1.70158; $c3 = $c1 + 1
        $back = 1 + $c3 * [Math]::Pow($t - 1, 3) + $c1 * [Math]::Pow($t - 1, 2)
        $form.Opacity = $ease
        $form.Location = [System.Drawing.Point]::new($targetX, [int][Math]::Round($targetY + ($slideFrom * (1 - $back))))
        if ($t -lt 1.0) { return }
        # WinForms rewrites the extended styles while animating Opacity; put
        # NOACTIVATE back (and keep TOOLWINDOW off) so the card keeps ignoring
        # focus but still accepts clicks for the rest of its life.
        $ex = [NotifyWin]::GetWindowLong($form.Handle, -20)
        $ex = $ex -band (-bnot 0x00000080)
        $null = [NotifyWin]::SetWindowLong($form.Handle, -20, $ex -bor 0x08000000)
        $script:settled = $true
        if (-not $script:gliding) { [NotifyWin]::StopPump(); return }
    }
    if ($script:gliding) {
        $delta = $script:scrollTarget - $script:scroll
        if ([Math]::Abs($delta) -lt 0.5) {
            $script:scroll = $script:scrollTarget
            $script:gliding = $false
            [NotifyWin]::StopPump()
        } else {
            $script:scroll += $delta * 0.28
        }
        $bodyBox.Invalidate()
    }
}

# Exit animation: fade out while sinking back toward the taskbar, ease-in cubic
# so it leaves slowly and then accelerates away. Every close path funnels here,
# and the window is only really closed when the fade lands, which is why the
# timers stop first — a reflow tick mid-fade would fight the slide for the
# window position.
$exitMs = 220
$exitSlide = px 24
$script:exiting = $false
$script:exitSw = $null
$script:exitFromOpacity = 1.0
$script:exitFromY = 0
$beginExit = {
    if ($script:exiting) { return }
    $script:exiting = $true
    $life.Stop()
    $reflow.Stop()
    if ($watch) { $watch.Stop() }
    $script:exitFromOpacity = $form.Opacity
    $script:exitFromY = $form.Location.Y
    $script:exitSw = [System.Diagnostics.Stopwatch]::StartNew()
    [NotifyWin]::StartPump($form.Handle, 4)
}

$life = New-Object System.Windows.Forms.Timer
$life.Interval = [Math]::Max(1, $Seconds) * 1000
$life.Add_Tick({ $life.Stop(); & $close })

# Every card re-derives its slot, because closing the front card promotes the
# next one to the fully drawn corner. Only after the entrance animation, which
# owns the position until it finishes.
#
# Z-order is not raised from here — a raise is a one-shot event and the last
# card to fire its timer would win, which is exactly the race that put the
# newest card in front. Instead each card re-inserts itself directly below the
# card one step older than it (HWND_TOP puts the front card at the head of the
# topmost band, which is where it belongs). Those are relative placements
# between adjacent pairs, so the whole deck comes out in age order regardless of
# which timer fires first.
$script:settled = $false
$reflow = New-Object System.Windows.Forms.Timer
$reflow.Interval = 300
$reflow.Add_Tick({
    if (-not $script:settled) { return }
    $y = Get-SlotY
    if ($form.Location.Y -ne $y) { $form.Location = [System.Drawing.Point]::new($targetX, $y) }
    $after = if ($script:prevHwnd -eq [IntPtr]::Zero -or -not [NotifyWin]::IsWindow($script:prevHwnd)) { [IntPtr]::Zero } else { $script:prevHwnd }
    $null = [NotifyWin]::SetWindowPos($form.Handle, $after, 0, 0, 0, 0, 0x0010 -bor 0x0001 -bor 0x0002)
})

# Approval/question cards mirror the in-app prompt: the plugin creates the
# dismiss file the moment the request resolves, so the card goes away exactly
# when the in-app UI is answered. A slow poll is plenty — the file appears at
# the moment of the decision, not on a schedule. The life timer still runs as a
# safety net so a lost signal cannot leave the card pinned forever.
$watch = $null
if ($DismissFile) {
    $watch = New-Object System.Windows.Forms.Timer
    $watch.Interval = 100
    $watch.Add_Tick({
        if (Test-Path -LiteralPath $DismissFile) { $watch.Stop(); & $close }
    })
}

# ShowDialog runs a real message loop, so the window is actually created and
# clicks land; the timers above bound how long it lives.
$null = $form.ShowDialog()
[NotifyWin]::StopPump()
$life.Dispose()
$reflow.Dispose()
if ($watch) { $watch.Dispose() }
$form.Dispose()
# Leaving the slot file behind would keep this card's place in the stack for
# good, so the next card would never reclaim the corner.
Remove-Item -LiteralPath $cardFile -Force -ErrorAction SilentlyContinue
$null = [NotifyWin]::timeEndPeriod(1)

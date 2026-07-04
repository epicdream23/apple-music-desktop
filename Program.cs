using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AppleMusicApp;

static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    static void Main()
    {
        SetCurrentProcessExplicitAppUserModelID("AppleMusicDesktop.WebPlayer");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

class WindowSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Max { get; set; }
    public bool Pin { get; set; }
    public bool DiscordRpc { get; set; } = true;
    public string DiscordClientId { get; set; }
}

class MainForm : Form
{
    const string HomeUrl = "https://music.apple.com/de/home";
    const int TopStripLogical = 3; // slim strip above the webview, used as the top resize grip

    static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleMusicPlayer");
    static readonly string SettingsPath = Path.Combine(DataDir, "window.json");

    // public Discord application id that displays as "Apple Music" (same one the
    // AMWin-RP project uses); override via DiscordClientId in window.json
    const string DefaultDiscordClientId = "1066220978406953012";

    WebView2 _web;
    WindowSettings _settings = new();
    DiscordRpc _rpc;
    string _lastRpcKey;
    long _lastRpcStartMs;
    bool _fullscreen;
    Rectangle _restoreBounds;
    bool _wasMaximized;
    bool _sentMaxState;
    long _lastDragTick;
    Point _lastDragPos;

    // ---- Win32 ----
    const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_SYSCOMMAND = 0x0112;
    const int HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    const int SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
    const uint TPM_RETURNCMD = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct NCCALCSIZE_PARAMS { public RECT rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public MainForm()
    {
        Text = "Apple Music";
        FormBorderStyle = FormBorderStyle.Sizable; // caption is stripped in WM_NCCALCSIZE, l/r/b frame stays native
        BackColor = Color.FromArgb(24, 24, 28);    // blends with the Apple Music dark background
        MinimumSize = new Size(480, 360);
        DoubleBuffered = true;

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        ApplySavedBounds();

        if (_settings.DiscordRpc)
            _rpc = new DiscordRpc(string.IsNullOrWhiteSpace(_settings.DiscordClientId)
                ? DefaultDiscordClientId : _settings.DiscordClientId);

        _web = new WebView2 { DefaultBackgroundColor = Color.FromArgb(24, 24, 28) };
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            try
            {
                // never background-throttle: without these, Chromium's window occlusion
                // tracking misreads the frameless window and caps rendering like an
                // inactive browser tab (fullscreen bypassed it, windowed mode didn't)
                var opts = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments =
                        "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling " +
                        "--disable-backgrounding-occluded-windows --disable-background-timer-throttling " +
                        "--disable-renderer-backgrounding"
                };
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), opts);
                await _web.EnsureCoreWebView2Async(env);
                var cw = _web.CoreWebView2;
                cw.Settings.IsStatusBarEnabled = false;
                cw.Settings.IsNonClientRegionSupportEnabled = true; // enables CSS app-region: drag
                cw.DocumentTitleChanged += (_, _) =>
                {
                    var t = cw.DocumentTitle;
                    Text = string.IsNullOrWhiteSpace(t) ? "Apple Music" : t;
                };
                cw.ContainsFullScreenElementChanged += (_, _) => SetFullscreen(cw.ContainsFullScreenElement);
                cw.NavigationCompleted += (_, _) => { SyncPinToPage(); _sentMaxState = false; SyncMaxStateToPage(); };
                cw.WebMessageReceived += OnWebMessage;
                cw.NewWindowRequested += (_, e) =>
                {
                    // keep Apple login/checkout popups inside; send everything else to the default browser
                    if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
                        !uri.Host.EndsWith("apple.com", StringComparison.OrdinalIgnoreCase) &&
                        !uri.Host.EndsWith("mzstatic.com", StringComparison.OrdinalIgnoreCase) &&
                        !uri.Host.EndsWith("itunes.com", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Handled = true;
                        try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                    }
                };
                await cw.AddScriptToExecuteOnDocumentCreatedAsync(GlassScript);
                cw.Navigate(HomeUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "WebView2 konnte nicht gestartet werden:\n\n" + ex.Message,
                    "Apple Music", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }

    // ---------- layout ----------

    int Scale(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    bool Framed => !_fullscreen && WindowState != FormWindowState.Maximized;
    int TopStrip => Framed ? Scale(TopStripLogical) : 0;

    void Relayout()
    {
        if (_web == null) return;
        int top = TopStrip;
        _web.SetBounds(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        Relayout();
        SyncMaxStateToPage();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }

    void SyncMaxStateToPage()
    {
        bool max = WindowState == FormWindowState.Maximized;
        if (max == _sentMaxState || _web?.CoreWebView2 == null) return;
        _sentMaxState = max;
        _ = _web.CoreWebView2.ExecuteScriptAsync(
            $"window.__amSetMax && window.__amSetMax({(max ? "true" : "false")})");
    }

    // ---------- window commands from the glass capsule ----------

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); } catch { return; }
        if (msg.Length > 0 && msg[0] == '{') { HandleNowPlaying(msg); return; }
        switch (msg)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
                ToggleMaximize();
                break;
            case "close":
                Close();
                break;
            case "pin":
                TopMost = !TopMost;
                SyncPinToPage();
                break;
            case "menu":
                ShowSystemMenuAtCursor();
                break;
            case "drag": // fallback path if app-region is unsupported
                HandleDragMessage();
                break;
        }
    }

    void HandleNowPlaying(string json)
    {
        if (_rpc == null) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!r.TryGetProperty("rpc", out _)) return;

            string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

            bool playing = r.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
            string title = Str("title");
            if (!playing || string.IsNullOrWhiteSpace(title))
            {
                if (_lastRpcKey != null) { _lastRpcKey = null; _rpc.Clear(); }
                return;
            }

            string artist = Str("artist"), album = Str("album"), art = Str("art"), url = Str("url");
            double duration = Num("duration"), position = Num("position");
            string key = title + "\n" + artist + "\n" + album;
            long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(position * 1000);

            // same song, no seek (start drift < 3 s) -> nothing changed, skip the resync
            if (key == _lastRpcKey && Math.Abs(startMs - _lastRpcStartMs) < 3000) return;
            _lastRpcKey = key;
            _lastRpcStartMs = startMs;
            _rpc.SetListening(title, artist, album, art, url, duration, position);
        }
        catch { }
    }

    void ToggleMaximize() =>
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    void SyncPinToPage() =>
        _ = _web?.CoreWebView2?.ExecuteScriptAsync(
            $"window.__amSetPinned && window.__amSetPinned({(TopMost ? "true" : "false")})");

    void ShowSystemMenuAtCursor()
    {
        var pt = Cursor.Position;
        int cmd = TrackPopupMenuEx(GetSystemMenu(Handle, false), TPM_RETURNCMD, pt.X, pt.Y, Handle, IntPtr.Zero);
        if (cmd != 0) SendMessage(Handle, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
    }

    void HandleDragMessage()
    {
        long now = Environment.TickCount64;
        var pos = Cursor.Position;
        bool dbl = now - _lastDragTick <= SystemInformation.DoubleClickTime &&
                   Math.Abs(pos.X - _lastDragPos.X) <= SystemInformation.DoubleClickSize.Width &&
                   Math.Abs(pos.Y - _lastDragPos.Y) <= SystemInformation.DoubleClickSize.Height;
        _lastDragTick = now;
        _lastDragPos = pos;
        if (dbl) { _lastDragTick = 0; ToggleMaximize(); return; }
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    // ---------- persistence ----------

    void ApplySavedBounds()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(SettingsPath));
                _settings = s;
                var r = new Rectangle(s.X, s.Y, s.W, s.H);
                if (r.Width >= 480 && r.Height >= 360 &&
                    Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                    if (s.Max) WindowState = FormWindowState.Maximized;
                    TopMost = s.Pin;
                    return;
                }
            }
        }
        catch { }
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 820);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        try
        {
            Directory.CreateDirectory(DataDir);
            var b = _fullscreen ? _restoreBounds
                  : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            bool max = _fullscreen ? _wasMaximized : WindowState == FormWindowState.Maximized;
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new WindowSettings
                {
                    X = b.X, Y = b.Y, W = b.Width, H = b.Height, Max = max, Pin = TopMost,
                    DiscordRpc = _settings.DiscordRpc, DiscordClientId = _settings.DiscordClientId
                }));
        }
        catch { }
        _rpc?.Dispose();
    }

    // ---------- fullscreen (for videos) ----------

    void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            _wasMaximized = WindowState == FormWindowState.Maximized;
            _restoreBounds = _wasMaximized ? RestoreBounds : Bounds;
            _fullscreen = true;
            if (_wasMaximized) WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            _fullscreen = false;
            if (_wasMaximized) WindowState = FormWindowState.Maximized;
            else Bounds = _restoreBounds;
        }
        Relayout();
        _ = _web?.CoreWebView2?.ExecuteScriptAsync(
            $"window.__amSetVisible && window.__amSetVisible({(on ? "false" : "true")})");
    }

    // ---------- window frame ----------

    int HitTestFrame(Point c)
    {
        if (!Framed || c.Y >= TopStrip || c.Y < 0) return HTCLIENT;
        int corner = Scale(12);
        if (c.X < corner) return HTTOPLEFT;
        if (c.X >= ClientSize.Width - corner) return HTTOPRIGHT;
        return HTTOP;
    }

    static Point PointFromLParam(IntPtr lParam) =>
        new(unchecked((short)(long)lParam), unchecked((short)((long)lParam >> 16)));

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCCALCSIZE when m.WParam != IntPtr.Zero:
            {
                if (_fullscreen) { m.Result = IntPtr.Zero; return; } // client = whole window
                var p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                int topBefore = p.rgrc0.Top;
                base.WndProc(ref m); // default: strips client by full frame incl. caption
                p = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
                p.rgrc0.Top = topBefore; // reclaim caption + top border
                if (IsZoomed(Handle))
                {
                    int inset = GetSystemMetricsForDpi(SM_CYSIZEFRAME, (uint)DeviceDpi)
                              + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)DeviceDpi);
                    p.rgrc0.Top = topBefore + inset;
                }
                Marshal.StructureToPtr(p, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }

            case WM_NCHITTEST:
            {
                var pt = PointToClient(PointFromLParam(m.LParam));
                int ht = HitTestFrame(pt);
                if (ht != HTCLIENT) { m.Result = (IntPtr)ht; return; }
                break;
            }
        }
        base.WndProc(ref m);
    }

    // ---------- injected liquid glass capsule ----------

    const string GlassScript = """
(function () {
    if (window.self !== window.top) return;
    if (!/(^|\.)music\.apple\.com$/i.test(location.hostname)) return;

    function build() {
        if (document.getElementById('amg-root') || !document.body) return;

        var style = document.createElement('style');
        style.id = 'amg-css';
        style.textContent =
            '#amg-root{position:fixed;top:0;left:0;right:0;height:0;z-index:2147483647;}' +

            /* invisible drag strip along the very top */
            '#amg-strip{position:fixed;top:0;left:0;right:0;height:10px;app-region:drag;-webkit-app-region:drag;}' +

            /* floating liquid glass capsule */
            '.amg-cap{position:fixed;top:10px;height:38px;display:flex;align-items:center;' +
            'border-radius:999px;overflow:hidden;' +
            '-webkit-user-select:none;user-select:none;cursor:default;' +
            'app-region:drag;-webkit-app-region:drag;' +
            'background:linear-gradient(115deg,rgba(255,255,255,.20),rgba(255,255,255,.06) 38%,rgba(255,255,255,.02) 62%,rgba(255,255,255,.12)),rgba(255,255,255,.07);' +
            '-webkit-backdrop-filter:blur(28px) saturate(200%) brightness(1.08);' +
            'backdrop-filter:blur(28px) saturate(200%) brightness(1.08);' +
            'box-shadow:inset 0 1px 0 rgba(255,255,255,.38),inset 0 -1px 0 rgba(255,255,255,.09),' +
            'inset 1px 0 0 rgba(255,255,255,.14),inset -1px 0 0 rgba(255,255,255,.14),' +
            '0 10px 30px rgba(0,0,0,.38),0 2px 8px rgba(0,0,0,.25);}' +

            '#amg-right{right:14px;padding:0 5px;gap:2px;will-change:transform;' +
            'transition:transform .55s cubic-bezier(.3,1.55,.5,1);}' + /* springy, GPU-composited */

            '.amg-b{width:36px;height:30px;border:0;padding:0;border-radius:999px;background:transparent;' +
            'app-region:no-drag;-webkit-app-region:no-drag;' +
            'display:flex;align-items:center;justify-content:center;cursor:default;flex:none;' +
            'transition:background .13s ease;}' +
            '.amg-b svg{stroke:rgba(255,255,255,.95);stroke-width:1.35;fill:none;stroke-linecap:round;stroke-linejoin:round;' +
            'filter:drop-shadow(0 1px 2px rgba(0,0,0,.4));}' +
            '.amg-b:hover{background:rgba(255,255,255,.24);}' +
            '.amg-b:active{background:rgba(255,255,255,.15);}' +
            '.amg-b.amg-on{background:rgba(255,255,255,.30);box-shadow:inset 0 1px 0 rgba(255,255,255,.28);}' +
            '.amg-b.amg-close:hover{background:rgba(255,69,58,.92);}' +
            '.amg-b.amg-close:active{background:rgba(205,54,45,.95);}' +

            /* hide the draggable page scrollbars; wheel/keyboard scrolling still works */
            '*::-webkit-scrollbar{width:0!important;height:0!important;display:none!important;}' +
            '*{scrollbar-width:none!important;}';
        (document.head || document.documentElement).appendChild(style);

        var root = document.createElement('div');
        root.id = 'amg-root';
        root.innerHTML =
            '<div id="amg-strip"></div>' +
            '<div class="amg-cap" id="amg-right">' +
              '<button class="amg-b" id="amg-pin" title="Immer im Vordergrund" tabindex="-1">' +
                '<svg width="12" height="12" viewBox="0 0 12 12">' +
                  '<path d="M6 1.4 a2.3 2.3 0 0 1 2.3 2.3 c0 1.5 -1.1 2 -1.1 3.1 h-2.4 c0 -1.1 -1.1 -1.6 -1.1 -3.1 A2.3 2.3 0 0 1 6 1.4 z"/>' +
                  '<path d="M6 6.8 v3.8"/>' +
                '</svg>' +
              '</button>' +
              '<button class="amg-b" id="amg-min" title="Minimieren" tabindex="-1">' +
                '<svg width="12" height="12" viewBox="0 0 12 12"><path d="M2 6 h8"/></svg>' +
              '</button>' +
              '<button class="amg-b" id="amg-max" title="Maximieren" tabindex="-1">' +
                '<svg id="amg-max-full" width="12" height="12" viewBox="0 0 12 12"><rect x="2" y="2" width="8" height="8" rx="2.4"/></svg>' +
                '<svg id="amg-max-rest" style="display:none" width="12" height="12" viewBox="0 0 12 12">' +
                  '<rect x="1.8" y="3.6" width="6.6" height="6.6" rx="1.8"/>' +
                  '<path d="M4 3.6 v-.4 a1.7 1.7 0 0 1 1.7 -1.7 h2.8 a1.7 1.7 0 0 1 1.7 1.7 v2.8 a1.7 1.7 0 0 1 -1.7 1.7 h-.4"/>' +
                '</svg>' +
              '</button>' +
              '<button class="amg-b amg-close" id="amg-close" title="Schlie&szlig;en" tabindex="-1">' +
                '<svg width="12" height="12" viewBox="0 0 12 12"><path d="M2.5 2.5 l7 7 M9.5 2.5 l-7 7"/></svg>' +
              '</button>' +
            '</div>';
        document.body.appendChild(root);

        var post = function (msg) { try { window.chrome.webview.postMessage(msg); } catch (e) { } };

        // fallback drag/menu when app-region support is unavailable
        root.addEventListener('mousedown', function (e) {
            if (e.button !== 0 || e.target.closest('.amg-b')) return;
            e.preventDefault();
            post('drag');
        });
        root.addEventListener('contextmenu', function (e) {
            if (e.target.closest('.amg-b')) return;
            e.preventDefault();
            post('menu');
        });

        document.getElementById('amg-pin').addEventListener('click', function () { post('pin'); });
        document.getElementById('amg-min').addEventListener('click', function () { post('minimize'); });
        document.getElementById('amg-max').addEventListener('click', function () { post('maximize'); });
        document.getElementById('amg-close').addEventListener('click', function () { post('close'); });

        window.__amSetMax = function (m) {
            var f = document.getElementById('amg-max-full');
            var r = document.getElementById('amg-max-rest');
            var b = document.getElementById('amg-max');
            if (f) f.style.display = m ? 'none' : '';
            if (r) r.style.display = m ? '' : 'none';
            if (b) b.title = m ? 'Verkleinern' : 'Maximieren';
        };
        window.__amSetVisible = function (v) {
            root.style.display = v ? '' : 'none';
        };
        window.__amSetPinned = function (v) {
            var b = document.getElementById('amg-pin');
            if (b) {
                b.classList.toggle('amg-on', !!v);
                b.title = v ? 'Nicht mehr im Vordergrund halten' : 'Immer im Vordergrund';
            }
        };

        // dynamic position: dodge page icons that live in the top-right corner.
        // Only truly visible icons count -- Apple keeps hover-revealed buttons
        // (e.g. the lyrics fullscreen toggle) in the DOM at opacity 0.
        var cap = document.getElementById('amg-right');
        var curOff = 0;
        var scanQueued = false;
        var lastScan = 0;
        var lastScroll = 0;
        var scrollTimer = null;
        var pendingOff = -1;
        var pendingCount = 0;

        function reallyVisible(el) {
            try {
                if (el.checkVisibility)
                    return el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true });
            } catch (e) { }
            return true;
        }

        function scan() {
            scanQueued = false;
            lastScan = performance.now();
            if (!cap || !cap.isConnected || document.hidden) return;
            if (root.style.display === 'none') return;              // hidden (video fullscreen)
            if (performance.now() - lastScroll < 250) return;       // hold still while scrolling
            var W = window.innerWidth;
            var minLeft = Infinity;
            // cheap region probe: hit-test a small grid of points in the top-right strip
            // instead of walking the whole DOM. elementFromPoint only ever returns what
            // is actually on top, so overlays and occlusion are handled for free.
            root.style.pointerEvents = 'none'; // don't let the capsule swallow the hit-test
            try {
                for (var row = 0; row < 2; row++) {
                    var py = row === 0 ? 22 : 48;
                    for (var px = W - 24; px >= W - 240; px -= 36) {
                        var hit = document.elementFromPoint(px, py);
                        if (!hit || root.contains(hit) || !hit.closest) continue;
                        var btn = hit.closest('button, a, [role="button"]');
                        if (!btn) continue;
                        var rc = btn.getBoundingClientRect();
                        if (rc.width < 5 || rc.height < 5) continue;         // collapsed
                        if (rc.width > 220 || rc.height > 56) continue;      // not icon-sized
                        if (rc.top < -5 || rc.bottom > 68) continue;         // outside the top strip
                        if (!reallyVisible(btn)) continue;                   // opacity-0 hover controls
                        if (rc.left < minLeft) minLeft = rc.left;
                    }
                }
            } finally {
                root.style.pointerEvents = '';
            }
            var off = 0;
            if (minLeft !== Infinity)
                off = Math.max(0, Math.min(306, Math.round(W - minLeft + 12) - 14));

            // debounce: only move once the same result shows up in two scans in a row,
            // so icons flying past during animations can't yank the capsule around
            if (Math.abs(off - curOff) <= 2) {
                pendingOff = -1;
                pendingCount = 0;
            } else if (pendingOff >= 0 && Math.abs(off - pendingOff) <= 2) {
                if (++pendingCount >= 2) {
                    curOff = off;
                    pendingOff = -1;
                    pendingCount = 0;
                    cap.style.transform = off ? 'translateX(-' + off + 'px)' : 'none';
                }
            } else {
                pendingOff = off;
                pendingCount = 1;
            }
        }

        function requestScan(minGap) {
            if (scanQueued || performance.now() - lastScan < minGap) return;
            scanQueued = true;
            requestAnimationFrame(scan); // measure after layout, never mid-frame
        }

        window.addEventListener('resize', function () { requestScan(0); });
        document.addEventListener('mousemove', function () { requestScan(200); }, { passive: true });
        // freeze during scrolling, settle once it stops
        function onScrollActivity() {
            lastScroll = performance.now();
            if (scrollTimer) clearTimeout(scrollTimer);
            scrollTimer = setTimeout(function () { requestScan(0); }, 300);
        }
        document.addEventListener('scroll', onScrollActivity, { capture: true, passive: true });
        document.addEventListener('wheel', onScrollActivity, { capture: true, passive: true });
        // view toggles (lyrics fullscreen etc.) happen on click; check right away and
        // again once the open/close animation has finished
        document.addEventListener('click', function () {
            requestScan(0);
            setTimeout(function () { requestScan(0); }, 500);
        }, { passive: true, capture: true });
        cap.addEventListener('transitionend', function () { requestScan(0); });
        setInterval(function () { requestScan(350); }, 400);
        requestScan(0);

        // survive SPA re-renders that replace the body content
        setInterval(function () {
            if (!root.isConnected && document.body) document.body.appendChild(root);
        }, 1500);
    }

    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', build);
    else
        build();

    // ---- now playing -> Discord rich presence (via host) ----
    function initRpc() {
        var mk = null;
        try { mk = window.MusicKit && window.MusicKit.getInstance ? window.MusicKit.getInstance() : null; } catch (e) { }
        if (!mk) { setTimeout(initRpc, 3000); return; }
        var send = function () {
            try {
                var item = mk.nowPlayingItem;
                var a = (item && item.attributes) || {};
                window.chrome.webview.postMessage(JSON.stringify({
                    rpc: 1,
                    playing: !!mk.isPlaying,
                    title: a.name || '',
                    artist: a.artistName || '',
                    album: a.albumName || '',
                    url: a.url || '',
                    duration: a.durationInMillis ? a.durationInMillis / 1000 : 0,
                    position: mk.currentPlaybackTime || 0,
                    art: (a.artwork && a.artwork.url)
                        ? a.artwork.url.replace('{w}', '512').replace('{h}', '512') : ''
                }));
            } catch (e) { }
        };
        try {
            mk.addEventListener('playbackStateDidChange', send);
            mk.addEventListener('nowPlayingItemDidChange', send);
        } catch (e) { }
        setInterval(send, 15000); // periodic position resync (catches seeks)
        send();
    }
    initRpc();
})();
""";
}

// Minimal Discord Rich Presence client over the local IPC named pipe.
class DiscordRpc : IDisposable
{
    readonly string _clientId;
    readonly object _sync = new();
    readonly System.Threading.Timer _timer;
    NamedPipeClientStream _pipe;
    Dictionary<string, object> _lastActivity;
    long _lastConnectAttempt = -60000;
    long _seq;
    volatile bool _disposed;

    public DiscordRpc(string clientId)
    {
        _clientId = clientId;
        // if Discord (re)starts later, reconnect and restore the last activity
        _timer = new System.Threading.Timer(_ => Heartbeat(), null, 5000, 20000);
    }

    static void WriteFrame(Stream s, int op, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var buf = new byte[8 + payload.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(payload.Length).CopyTo(buf, 4);
        payload.CopyTo(buf, 8);
        s.Write(buf, 0, buf.Length);
        s.Flush();
    }

    static string ReadFrame(Stream s)
    {
        var hdr = ReadExact(s, 8);
        int len = BitConverter.ToInt32(hdr, 4);
        if (len < 0 || len > (1 << 20)) throw new IOException("bad frame");
        return Encoding.UTF8.GetString(ReadExact(s, len));
    }

    static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = s.Read(buf, off, n - off);
            if (r <= 0) throw new IOException("pipe closed");
            off += r;
        }
        return buf;
    }

    bool EnsureConnected()
    {
        if (_pipe is { IsConnected: true }) return true;
        long now = Environment.TickCount64;
        if (now - _lastConnectAttempt < 15000) return false; // retry cooldown
        _lastConnectAttempt = now;
        _pipe?.Dispose();
        _pipe = null;
        for (int i = 0; i < 10 && !_disposed; i++)
        {
            NamedPipeClientStream p = null;
            try
            {
                p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
                p.Connect(200);
                WriteFrame(p, 0, JsonSerializer.Serialize(new { v = 1, client_id = _clientId }));
                ReadFrame(p); // READY dispatch
                _pipe = p;
                var captured = p;
                Task.Run(() => Drain(captured)); // discard responses so the pipe never clogs
                return true;
            }
            catch { p?.Dispose(); }
        }
        return false;
    }

    void Drain(NamedPipeClientStream p)
    {
        try { while (true) ReadFrame(p); } catch { }
        lock (_sync) { if (_pipe == p) _pipe = null; }
        try { p.Dispose(); } catch { }
    }

    void Send(long seq, Dictionary<string, object> activity)
    {
        lock (_sync)
        {
            if (_disposed || seq != Interlocked.Read(ref _seq)) return; // superseded
            _lastActivity = activity;
            if (!EnsureConnected()) return;
            try { WriteFrame(_pipe, 1, BuildSetActivity(activity)); }
            catch
            {
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    static string BuildSetActivity(Dictionary<string, object> activity)
    {
        var args = new Dictionary<string, object> { ["pid"] = Environment.ProcessId };
        if (activity != null) args["activity"] = activity;
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            ["nonce"] = Guid.NewGuid().ToString()
        });
    }

    void Heartbeat()
    {
        lock (_sync)
        {
            if (_disposed || _lastActivity == null) return;
            if (_pipe is { IsConnected: true }) return; // healthy
            if (!EnsureConnected()) return;
            try { WriteFrame(_pipe, 1, BuildSetActivity(_lastActivity)); }
            catch
            {
                try { _pipe?.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    static string Cap(string s, int n) =>
        string.IsNullOrEmpty(s) ? null : (s.Length <= n ? s : s.Substring(0, n));

    public void SetListening(string title, string artist, string album, string art, string url,
                             double durationSec, double positionSec)
    {
        long seq = Interlocked.Increment(ref _seq);
        long startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)(positionSec * 1000);
        string details = Cap(title, 128);
        if (details.Length < 2) details += "  "; // Discord requires >= 2 chars
        var activity = new Dictionary<string, object> { ["type"] = 2, ["details"] = details };
        var state = Cap(artist, 128);
        if (state != null) activity["state"] = state;
        var ts = new Dictionary<string, object> { ["start"] = startMs };
        if (durationSec > 1) ts["end"] = startMs + (long)(durationSec * 1000);
        activity["timestamps"] = ts;
        var assets = new Dictionary<string, object>();
        var artUrl = Cap(art, 256);
        if (artUrl != null) assets["large_image"] = artUrl;
        var albumText = Cap(album, 128);
        if (albumText is { Length: >= 2 }) assets["large_text"] = albumText;
        if (assets.Count > 0) activity["assets"] = assets;
        if (url != null && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            activity["buttons"] = new[]
            {
                new Dictionary<string, object> { ["label"] = "In Apple Music ansehen", ["url"] = Cap(url, 512) }
            };
        Task.Run(() => Send(seq, activity));
    }

    public void Clear()
    {
        long seq = Interlocked.Increment(ref _seq);
        Task.Run(() => Send(seq, null));
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        lock (_sync)
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
        }
    }
}

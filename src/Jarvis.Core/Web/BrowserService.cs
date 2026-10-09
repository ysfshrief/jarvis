using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Settings;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Web;

/// <summary>An interactive element on the current page, numbered so the AI can refer to it.</summary>
public sealed record PageElement
{
    public int Id { get; init; }
    public string Tag { get; init; } = "";
    public string Type { get; init; } = "";
    public string Role { get; init; } = "";
    public string Label { get; init; } = "";
    public string Href { get; init; } = "";
    public bool InForm { get; init; }
    public string FormMethod { get; init; } = "";
    public string FormAction { get; init; } = "";
    public bool IsSubmit { get; init; }
    public bool IsPassword { get; init; }
    public string Autocomplete { get; init; } = "";
    public bool Disabled { get; init; }
    public string Value { get; init; } = "";

    public bool IsLink => Tag == "a" && Href.Length > 0;
    public bool IsTextField => Tag is "textarea" || (Tag == "input" && Type is not ("submit" or "button" or "reset" or "checkbox" or "radio" or "image" or "file" or "range" or "color"));

    /// <summary>"[12] button “Send”" — the compact form the model reads.</summary>
    public string Describe()
    {
        var kind = IsLink ? "link" : Tag == "input" ? $"input:{(Type.Length > 0 ? Type : "text")}" : Role.Length > 0 ? Role : Tag;
        var extra = IsLink ? $" → {ShortUrl(Href)}" : Value.Length > 0 ? $" = “{Value}”" : "";
        return $"[{Id}] {kind} “{Label}”{extra}{(Disabled ? " (disabled)" : "")}";
    }

    private static string ShortUrl(string url) => url.Length > 80 ? url[..77] + "…" : url;
}

public sealed record PageSnapshot(string Url, string Title, string Text, IReadOnlyList<PageElement> Elements);

/// <summary>
/// JARVIS's own browser: Microsoft Edge (or Chrome/Chromium) in a separate profile, driven over the
/// DevTools protocol. The window is visible by default so the user always sees what happens. Page
/// scripts run in an isolated world so a page can't tamper with how JARVIS reads it.
/// </summary>
public sealed class BrowserService(JarvisPaths paths, ISettingsStore settings, IEventBus events, ILogger<BrowserService> logger) : IAsyncDisposable, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private CdpConnection? _cdp;
    private string? _session;
    private string? _targetId;
    private Dictionary<int, PageElement> _elements = [];

    public string ProfileDir => Path.Combine(paths.DataDir, "browser");
    public bool IsRunning => _cdp?.IsOpen == true && _session is not null;
    public string? CurrentUrl { get; private set; }

    /// <summary>The element as last seen by the AI (used to grade the risk of acting on it).</summary>
    public PageElement? Known(int id) => _elements.GetValueOrDefault(id);

    /// <summary>Path of the browser JARVIS would use, or null when none is installed.</summary>
    public string? Locate()
    {
        var configured = settings.Current.Web.BrowserPath;
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var env = Environment.GetEnvironmentVariable("JARVIS_BROWSER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }
        }
        else
        {
            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in new[] { "microsoft-edge", "microsoft-edge-stable", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" })
                candidates.AddRange(pathDirs.Select(d => Path.Combine(d, name)));
            candidates.Add("/opt/pw-browsers/chromium");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<PageSnapshot> NavigateAsync(string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureAsync(ct).ConfigureAwait(false);
            var r = await SendAsync("Page.navigate", new { url }, ct).ConfigureAwait(false);
            if (r.TryGetProperty("errorText", out var err) && err.GetString() is { Length: > 0 } e)
                throw new CdpException(e.Replace("net::", ""));
            await WaitForLoadAsync(ct).ConfigureAwait(false);
            return await SnapshotLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PageSnapshot> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning) throw new CdpException("No page is open in JARVIS's browser yet.");
            return await SnapshotLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-reads one element directly from the page (null when it's gone).</summary>
    public async Task<PageElement?> InspectAsync(int id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning) return null;
            var json = await EvaluateAsync($"(() => {{ {DescribeJs} const e = document.querySelector('[data-jarvis-id=\"{id}\"]'); return e ? JSON.stringify(describe(e, {id})) : null; }})()", ct).ConfigureAwait(false);
            return json is null ? null : JsonSerializer.Deserialize<PageElement>(json, Json);
        }
        finally { _gate.Release(); }
    }

    public async Task<PageSnapshot> ClickAsync(int id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pos = await EvaluateAsync($$"""
                (() => {
                  const e = document.querySelector('[data-jarvis-id="{{id}}"]');
                  if (!e) return null;
                  e.scrollIntoView({ block: 'center', inline: 'center' });
                  const r = e.getBoundingClientRect();
                  return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2 });
                })()
                """, ct).ConfigureAwait(false) ?? throw new CdpException($"Element {id} is no longer on the page.");
            var p = JsonDocument.Parse(pos).RootElement;
            double x = p.GetProperty("x").GetDouble(), y = p.GetProperty("y").GetDouble();
            await SendAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct).ConfigureAwait(false);
            await SendAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct).ConfigureAwait(false);
            await SendAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            await WaitForLoadAsync(ct).ConfigureAwait(false);
            return await SnapshotLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PageSnapshot> TypeAsync(int id, string text, bool submit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ok = await EvaluateAsync($$"""
                (() => {
                  const e = document.querySelector('[data-jarvis-id="{{id}}"]');
                  if (!e) return null;
                  e.scrollIntoView({ block: 'center' });
                  e.focus();
                  if (typeof e.select === 'function') e.select();
                  else if (e.isContentEditable) document.execCommand('selectAll');
                  return 'ok';
                })()
                """, ct).ConfigureAwait(false) ?? throw new CdpException($"Element {id} is no longer on the page.");
            await SendAsync("Input.insertText", new { text }, ct).ConfigureAwait(false);
            if (submit)
            {
                await SendAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = "\r" }, ct).ConfigureAwait(false);
                await SendAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13 }, ct).ConfigureAwait(false);
                await Task.Delay(400, ct).ConfigureAwait(false);
                await WaitForLoadAsync(ct).ConfigureAwait(false);
            }
            return await SnapshotLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PageSnapshot> BackAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning) throw new CdpException("No page is open in JARVIS's browser yet.");
            await EvaluateAsync("history.back(), 'ok'", ct).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
            await WaitForLoadAsync(ct).ConfigureAwait(false);
            return await SnapshotLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> ScreenshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsRunning) throw new CdpException("No page is open in JARVIS's browser yet.");
            var r = await SendAsync("Page.captureScreenshot", new { format = "png" }, ct).ConfigureAwait(false);
            return Convert.FromBase64String(r.GetProperty("data").GetString() ?? "");
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await ShutdownLockedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // ---- internals ----

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (IsRunning) return;
        await ShutdownLockedAsync(keepProcess: true).ConfigureAwait(false);
        Directory.CreateDirectory(ProfileDir);
        var portFile = Path.Combine(ProfileDir, "DevToolsActivePort");

        // A JARVIS browser left open from before is reused rather than fighting over the profile.
        if (await TryConnectAsync(portFile, ct).ConfigureAwait(false) is not { } cdp)
        {
            var exe = Locate() ?? throw new CdpException("No supported browser found (Microsoft Edge, Chrome or Chromium).");
            try { File.Delete(portFile); } catch { }
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in LaunchArgs()) psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new CdpException("The browser didn't start.");
            var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) { stderr.Enqueue(line); while (stderr.Count > 8) stderr.TryDequeue(out string? _); } };
            _process.OutputDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();
            logger.LogInformation("Started browser {Exe} (pid {Pid})", exe, _process.Id);
            var deadline = DateTime.UtcNow.AddSeconds(60); // a cold first start on a slow PC or CI runner can take a while
            while ((cdp = await TryConnectAsync(portFile, ct).ConfigureAwait(false)) is null)
            {
                if (_process.HasExited)
                {
                    var why = string.Join(" | ", stderr).Trim();
                    logger.LogWarning("Browser exited at start (code {Code}): {Stderr}", _process.ExitCode, why);
                    throw new CdpException("The browser closed right after starting. If a JARVIS browser window is already open, close it and try again." +
                                           (why.Length > 0 ? $" (Browser said: {(why.Length > 300 ? why[..300] + "…" : why)})" : ""));
                }
                if (DateTime.UtcNow > deadline) throw new CdpException("The browser didn't open its control channel in time.");
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        _cdp = cdp;
        _cdp.Event += OnEvent;

        var targets = await _cdp.SendAsync("Target.getTargets", ct: ct).ConfigureAwait(false);
        _targetId = targets.GetProperty("targetInfos").EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page" && !(t.GetProperty("url").GetString() ?? "").StartsWith("devtools:"))
            .Select(t => t.GetProperty("targetId").GetString()).FirstOrDefault();
        _targetId ??= (await _cdp.SendAsync("Target.createTarget", new { url = "about:blank" }, ct: ct).ConfigureAwait(false)).GetProperty("targetId").GetString();
        var attached = await _cdp.SendAsync("Target.attachToTarget", new { targetId = _targetId, flatten = true }, ct: ct).ConfigureAwait(false);
        _session = attached.GetProperty("sessionId").GetString();
        await SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        await SendAsync("Page.bringToFront", null, ct).ConfigureAwait(false);
        events.Publish(EventTypes.BrowserChanged, new { running = true });
    }

    private IEnumerable<string> LaunchArgs()
    {
        yield return "--remote-debugging-port=0";
        yield return $"--user-data-dir={ProfileDir}";
        yield return "--no-first-run";
        yield return "--no-default-browser-check";
        yield return "--disable-default-apps";
        yield return "--window-size=1280,900";
        if (settings.Current.Web.Headless) yield return "--headless=new";
        // Chromium refuses to sandbox as root (containers, CI); never needed on Windows.
        if (OperatingSystem.IsLinux() && Environment.UserName == "root") yield return "--no-sandbox";
        yield return "about:blank";
    }

    private static async Task<CdpConnection?> TryConnectAsync(string portFile, CancellationToken ct)
    {
        if (!File.Exists(portFile)) return null;
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false); }
        catch (IOException) { return null; }
        if (lines.Length < 2 || !int.TryParse(lines[0], out var port)) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await CdpConnection.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or OperationCanceledException or System.Net.Http.HttpRequestException) { return null; }
    }

    private void OnEvent(string method, JsonElement p, string? session)
    {
        if (method is "Target.targetDestroyed" && p.TryGetProperty("targetId", out var t) && t.GetString() == _targetId ||
            method is "Target.detachedFromTarget" && p.TryGetProperty("sessionId", out var s) && s.GetString() == _session)
        {
            _session = null;
            _elements = [];
        }
    }

    private Task<JsonElement> SendAsync(string method, object? p, CancellationToken ct) =>
        (_cdp ?? throw new CdpException("The browser isn't running.")).SendAsync(method, p, _session ?? throw new CdpException("The JARVIS browser tab was closed."), ct);

    /// <summary>Evaluates in an isolated world: same DOM, but the page's own scripts can't interfere.</summary>
    private async Task<string?> EvaluateAsync(string expression, CancellationToken ct)
    {
        var tree = await SendAsync("Page.getFrameTree", null, ct).ConfigureAwait(false);
        var frameId = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        var world = await SendAsync("Page.createIsolatedWorld", new { frameId, worldName = "jarvis" }, ct).ConfigureAwait(false);
        var contextId = world.GetProperty("executionContextId").GetInt32();
        var r = await SendAsync("Runtime.evaluate", new { expression, contextId, returnByValue = true, awaitPromise = true }, ct).ConfigureAwait(false);
        if (r.TryGetProperty("exceptionDetails", out var ex))
            throw new CdpException("Page script failed: " + (ex.TryGetProperty("text", out var tx) ? tx.GetString() : "error"));
        var v = r.GetProperty("result");
        return v.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
    }

    private async Task WaitForLoadAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await EvaluateAsync("document.readyState", ct).ConfigureAwait(false) == "complete") return;
            }
            catch (CdpException) { /* mid-navigation: the frame is being replaced */ }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private async Task<PageSnapshot> SnapshotLockedAsync(CancellationToken ct)
    {
        var json = await EvaluateAsync(SnapshotJs, ct).ConfigureAwait(false) ?? throw new CdpException("Couldn't read the page.");
        var snap = JsonSerializer.Deserialize<PageSnapshot>(json, Json) ?? throw new CdpException("Couldn't read the page.");
        _elements = snap.Elements.ToDictionary(e => e.Id);
        CurrentUrl = snap.Url;
        events.Publish(EventTypes.BrowserChanged, new { running = true, url = snap.Url, title = snap.Title });
        return snap;
    }

    private async Task ShutdownLockedAsync(bool keepProcess = false)
    {
        if (_cdp is not null)
        {
            _cdp.Event -= OnEvent;
            if (!keepProcess) try { await _cdp.SendAsync("Browser.close", timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            await _cdp.DisposeAsync().ConfigureAwait(false);
        }
        _cdp = null;
        _session = null;
        _elements = [];
        if (!keepProcess && _process is { HasExited: false })
        {
            if (!_process.WaitForExit(3000)) try { _process.Kill(entireProcessTree: true); } catch { }
        }
        if (!keepProcess)
        {
            _process = null;
            CurrentUrl = null;
            events.Publish(EventTypes.BrowserChanged, new { running = false });
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); } catch { }
    }

    public void Dispose()
    {
        try { CloseAsync().GetAwaiter().GetResult(); } catch { }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string DescribeJs = """
        const clean = s => (s || '').replace(/\s+/g, ' ').trim();
        const label = e => {
          let t = e.getAttribute('aria-label') || '';
          if (!t && e.labels && e.labels.length) t = e.labels[0].innerText;
          if (!t && e.tagName === 'INPUT' && ['submit', 'button', 'reset'].includes((e.type || '').toLowerCase())) t = e.value;
          if (!t && !['INPUT', 'TEXTAREA', 'SELECT'].includes(e.tagName)) t = e.innerText;
          if (!t) t = e.getAttribute('placeholder') || e.getAttribute('title') || e.getAttribute('name') || '';
          if (!t && e.querySelector) { const img = e.querySelector('img[alt]'); if (img) t = img.alt; }
          return clean(t).slice(0, 120);
        };
        const describe = (e, id) => {
          const tag = e.tagName.toLowerCase();
          const type = (e.getAttribute('type') || '').toLowerCase();
          const form = e.closest('form');
          const isSubmit = (tag === 'button' && (type === '' || type === 'submit') && !!form) || (tag === 'input' && (type === 'submit' || type === 'image'));
          return { id, tag, type, role: e.getAttribute('role') || '', label: label(e), href: tag === 'a' ? (e.href || '') : '',
            inForm: !!form, formMethod: form ? (form.getAttribute('method') || 'get').toLowerCase() : '', formAction: form ? (form.action || '') : '',
            isSubmit, isPassword: type === 'password', autocomplete: (e.getAttribute('autocomplete') || '').toLowerCase(), disabled: !!e.disabled,
            value: (tag === 'input' || tag === 'textarea') && type !== 'password' ? clean(e.value).slice(0, 80) : '' };
        };
        """;

    private const string SnapshotJs = "(() => {" + DescribeJs + """
          document.querySelectorAll('[data-jarvis-id]').forEach(e => e.removeAttribute('data-jarvis-id'));
          const sel = 'a[href], button, input:not([type=hidden]), textarea, select, summary, [role=button], [role=link], [role=tab], [role=menuitem], [role=checkbox], [role=switch], [role=option], [contenteditable=""], [contenteditable=true]';
          const visible = e => { const r = e.getBoundingClientRect(); if (r.width < 2 || r.height < 2) return false; const st = getComputedStyle(e); return st.visibility !== 'hidden' && st.display !== 'none' && st.opacity !== '0'; };
          const out = [];
          for (const e of document.querySelectorAll(sel)) {
            if (out.length >= 150) break;
            if (!visible(e)) continue;
            const id = out.length + 1;
            e.setAttribute('data-jarvis-id', String(id));
            out.push(describe(e, id));
          }
          return JSON.stringify({ url: location.href, title: document.title, text: clean(document.body ? document.body.innerText : '').slice(0, 12000), elements: out });
        })()
        """;
}

/// <summary>
/// How risky acting on a page element is. Anything that sends, buys, publishes, deletes, books,
/// subscribes or changes an account is critical (always asks); typing into password or payment fields
/// is critical; other button presses and typing are sensitive; following a plain link is safe.
/// Page text can lie, so a submit button of a POST form is critical whatever its label says.
/// </summary>
public static partial class BrowserRisk
{
    public static RiskAssessment ForClick(PageElement e)
    {
        var label = e.Label.Length > 0 ? $"“{e.Label}”" : $"element {e.Id}";
        if (e.IsLink && !e.Href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return Consequential(e.Label) ? new(RiskLevel.Sensitive, $"Open the link {label} in the browser") : new(RiskLevel.Safe, $"Open the link {label} in the browser");
        if (Consequential(e.Label)) return new(RiskLevel.Critical, $"Press {label} on {Host(e)} — this may send, buy, publish, delete or change something");
        if (e.IsSubmit && e.FormMethod == "post") return new(RiskLevel.Critical, $"Submit the form on {Host(e)} ({label})");
        return new(RiskLevel.Sensitive, $"Press {label} in the browser");
    }

    public static RiskAssessment ForType(PageElement e, bool submit)
    {
        var label = e.Label.Length > 0 ? $"“{e.Label}”" : $"field {e.Id}";
        if (e.IsPassword || SensitiveAutocomplete().IsMatch(e.Autocomplete))
            return new(RiskLevel.Critical, $"Type into the password/payment field {label} on {Host(e)}");
        if (submit && (e.FormMethod == "post" || Consequential(e.Label)))
            return new(RiskLevel.Critical, $"Type into {label} and submit the form on {Host(e)}");
        return new(RiskLevel.Sensitive, submit ? $"Type into {label} and press Enter" : $"Type into {label} in the browser");
    }

    public static bool Consequential(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        return Actions().IsMatch(label) || ArabicActions().IsMatch(TextNormalizer.Normalize(label));
    }

    private static string Host(PageElement e) =>
        Uri.TryCreate(e.FormAction.Length > 0 ? e.FormAction : e.Href, UriKind.Absolute, out var u) ? u.Host : "this page";

    [GeneratedRegex(@"\b(?:send|submit|post|publish|tweet|reply|share|buy|purchase|order|checkout|check out|pay|payment|donate|subscribe|sign up|signup|register|create account|delete|remove|erase|unsubscribe|cancel (?:subscription|order|account|plan)|confirm|transfer|withdraw|book|reserve|apply (?:now|for|to)|submit application|accept (?:offer|invitation|request)|save changes|update (?:password|email|account|profile)|change password|place order|follow|connect|invite|upload)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Actions();

    [GeneratedRegex(@"ارسال|ابعت|ارسل|انشر|نشر|شراء|اشتري|ادفع|دفع|اطلب|تاكيد|اكد|احذف|حذف|امسح|اشترك|اشتراك|تسجيل|سجل|حجز|احجز|تحويل|تقديم|موافق|اوافق|حفظ|رفع")]
    private static partial Regex ArabicActions();

    [GeneratedRegex(@"cc-|one-time-code|current-password|new-password", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveAutocomplete();
}

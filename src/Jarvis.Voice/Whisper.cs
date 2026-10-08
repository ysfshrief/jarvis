using System.Diagnostics;
using System.Text;
using Jarvis.Core;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Settings;
using Jarvis.Core.Voice;
using Microsoft.Extensions.Logging;
using Whisper.net;

namespace Jarvis.Voice;

public sealed record SpeechModelInfo(string Name, string File, long ApproxBytes, string Description, bool Installed, long? SizeBytes);

/// <summary>
/// Downloads and tracks local Whisper models (ggml format from the whisper.cpp project, MIT licensed).
/// Models are fetched on demand, never bundled, and live in the JARVIS data folder.
/// </summary>
public sealed class SpeechModelManager(JarvisPaths paths, HttpClient http, IEventBus events, ILogger<SpeechModelManager> logger)
{
    public const string DownloadBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static readonly (string Name, long Bytes, string Description)[] Catalog =
    [
        ("tiny", 77_700_000, "Fastest, lowest accuracy. Good for wake-word spotting."),
        ("base", 147_900_000, "Balanced default. Decent English, fair Arabic."),
        ("small", 487_600_000, "Noticeably better Arabic. Needs a reasonable CPU."),
        ("medium", 1_533_800_000, "Best accuracy here; slow without a GPU."),
    ];

    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public string PathFor(string name) => Path.Combine(paths.ModelsDir, $"ggml-{name}.bin");

    public bool IsInstalled(string name) => File.Exists(PathFor(name)) && new FileInfo(PathFor(name)).Length > 1_000_000;

    public IReadOnlyList<SpeechModelInfo> List() => Catalog.Select(c =>
    {
        var installed = IsInstalled(c.Name);
        return new SpeechModelInfo(c.Name, $"ggml-{c.Name}.bin", c.Bytes, c.Description, installed,
            installed ? new FileInfo(PathFor(c.Name)).Length : null);
    }).ToList();

    public bool IsDownloading { get; private set; }

    public async Task DownloadAsync(string name, CancellationToken ct)
    {
        if (Catalog.All(c => c.Name != name)) throw new ArgumentException($"Unknown speech model '{name}'.");
        if (IsInstalled(name)) return;
        if (!await _downloadGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Another speech model download is already running.");
        IsDownloading = true;
        var target = PathFor(name);
        var tmp = target + ".part";
        try
        {
            logger.LogInformation("Downloading Whisper model {Model}", name);
            using var resp = await http.GetAsync(DownloadBase + $"ggml-{name}.bin", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReport = Stopwatch.StartNew();
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (lastReport.ElapsedMilliseconds > 500)
                    {
                        events.Publish(EventTypes.VoiceModelProgress, new { model = name, bytes = done, total, done = false });
                        lastReport.Restart();
                    }
                }
            }
            File.Move(tmp, target, overwrite: true);
            events.Publish(EventTypes.VoiceModelProgress, new { model = name, bytes = new FileInfo(target).Length, total, done = true });
        }
        catch (Exception ex)
        {
            events.Publish(EventTypes.VoiceModelProgress, new { model = name, error = ex.Message, done = true });
            try { File.Delete(tmp); } catch { }
            throw;
        }
        finally
        {
            IsDownloading = false;
            _downloadGate.Release();
        }
    }
}

/// <summary>
/// Local, offline speech recognition with whisper.cpp (via Whisper.net). Supports English and
/// Arabic (including Egyptian dialect, with model-dependent accuracy) with automatic detection.
/// </summary>
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly SpeechModelManager _models;
    private readonly ILogger<WhisperSpeechToText> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loadedModel;
    private string? _loadError;

    public WhisperSpeechToText(ISettingsStore settings, SpeechModelManager models, ILogger<WhisperSpeechToText> logger)
    {
        _settings = settings;
        _models = models;
        _logger = logger;
    }

    public string EngineName => "whisper.cpp" + (_loadedModel is null ? "" : $" ({_loadedModel})");

    public bool IsReady => _models.IsInstalled(_settings.Current.Voice.SttModel) && _loadError is null;

    public string? StatusMessage =>
        _loadError ?? (_models.IsInstalled(_settings.Current.Voice.SttModel)
            ? null
            : $"Speech model '{_settings.Current.Voice.SttModel}' is not downloaded yet (Settings → Voice).");

    public async Task<Transcript> TranscribeAsync(float[] samples, Lang? hint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var factory = EnsureLoaded();
            var builder = factory.CreateBuilder().WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8));
            builder = hint switch
            {
                Lang.Ar => builder.WithLanguage("ar"),
                Lang.En => builder.WithLanguage("en"),
                _ => builder.WithLanguageDetection(),
            };
            // A short prompt biases recognition toward the assistant's name and common app names.
            builder = builder.WithPrompt("Jarvis, جارفيس. Open VS Code, Chrome, Calculator. افتح، اقفل، فكرني.");
            await using var processor = builder.Build();

            var text = new StringBuilder();
            string? language = null;
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                text.Append(segment.Text);
                language ??= segment.Language;
            }

            var cleaned = Clean(text.ToString());
            var lang = language switch
            {
                "ar" => Lang.Ar,
                "en" => Lang.En,
                _ => (Lang?)LanguageDetector.Detect(cleaned),
            };
            return new Transcript(cleaned, lang, null, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhisperFactory EnsureLoaded()
    {
        var model = _settings.Current.Voice.SttModel;
        if (_factory is not null && _loadedModel == model) return _factory;
        if (!_models.IsInstalled(model))
            throw new InvalidOperationException($"Speech model '{model}' is not downloaded yet.");
        try
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(_models.PathFor(model));
            _loadedModel = model;
            _loadError = null;
            _logger.LogInformation("Loaded Whisper model {Model}", model);
            return _factory;
        }
        catch (Exception ex)
        {
            _loadError = $"Couldn't load the speech model: {ex.Message}";
            throw;
        }
    }

    /// <summary>Whisper emits markers like "[BLANK_AUDIO]" or "(music)" for non-speech.</summary>
    internal static string Clean(string text)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
    }

    public void Dispose() => _factory?.Dispose();
}

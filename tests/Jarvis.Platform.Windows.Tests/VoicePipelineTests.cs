using Jarvis.Core;
using Jarvis.Core.Agent;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Events;
using Jarvis.Core.Language;
using Jarvis.Core.Settings;
using Jarvis.Core.Voice;
using Jarvis.Platform.Windows;
using Jarvis.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Media.SpeechSynthesis;
using Xunit.Abstractions;

namespace Jarvis.Platform.Windows.Tests;

/// <summary>
/// End-to-end check of local speech recognition: Windows TTS speaks a command, Whisper (tiny model,
/// downloaded on first run) transcribes it, the wake word is found, and the agent executes it.
/// Needs internet for the one-time model download; skips cleanly when no Windows voice is installed.
/// </summary>
public sealed class VoicePipelineTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string ModelCache = Path.Combine(Path.GetTempPath(), "jarvis-whisper-cache");
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "jarvis-voice-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task Spoken_command_is_transcribed_and_executed()
    {
        if (SpeechSynthesizer.AllVoices.All(v => !v.Language.StartsWith("en")))
        {
            output.WriteLine("No English Windows voice installed; skipping.");
            return;
        }

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        sc.AddJarvisCore(new JarvisPaths(_dataDir));
        sc.AddWindowsPlatform();
        sc.AddJarvisVoice();
        using var sp = sc.BuildServiceProvider();
        sp.GetRequiredService<ISettingsStore>().Update(s => { s.Voice.SttModel = "tiny"; s.Ai.Providers = []; });
        sp.GetRequiredService<ConnectivityMonitor>().Set(true);

        // One-time model download, cached across test runs on the same machine.
        var models = sp.GetRequiredService<SpeechModelManager>();
        var cached = Path.Combine(ModelCache, "ggml-tiny.bin");
        Directory.CreateDirectory(ModelCache);
        if (!File.Exists(cached))
        {
            await models.DownloadAsync("tiny", CancellationToken.None);
            File.Copy(models.PathFor("tiny"), cached, overwrite: true);
        }
        else
        {
            File.Copy(cached, models.PathFor("tiny"), overwrite: true);
        }

        var samples = await SynthesizeAsync("Jarvis, open calculator.");
        output.WriteLine($"Synthesized {samples.Length / 16000.0:0.0}s of speech");

        var stt = sp.GetRequiredService<ISpeechToText>();
        Assert.True(stt.IsReady, stt.StatusMessage);
        var transcript = await stt.TranscribeAsync(samples, null, CancellationToken.None);
        output.WriteLine($"Transcript: \"{transcript.Text}\" ({transcript.Language}, {transcript.Duration.TotalMilliseconds:0} ms)");

        Assert.True(WakeWordMatcher.TryMatch(transcript.Text, ["jarvis"], out var command), $"wake word not found in \"{transcript.Text}\"");
        Assert.Contains("calculat", command);

        foreach (var p in System.Diagnostics.Process.GetProcessesByName("CalculatorApp")) { try { p.Kill(); } catch { } }
        var result = await sp.GetRequiredService<AgentOrchestrator>().HandleAsync(new UserInput(command, null, InputSource.Voice));
        output.WriteLine($"Agent: {result.Reply}");
        Assert.Equal("app_open", Assert.Single(result.Steps).Tool);
        Assert.True(result.Success, result.Reply);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("CalculatorApp").Concat(System.Diagnostics.Process.GetProcessesByName("calc")).Concat(System.Diagnostics.Process.GetProcessesByName("win32calc"))) { try { p.Kill(); } catch { } }
    }

    /// <summary>Windows TTS → WAV → 16 kHz mono float, the same format the microphone path produces.</summary>
    private static async Task<float[]> SynthesizeAsync(string text)
    {
        using var synth = new SpeechSynthesizer();
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en-US")) ?? SpeechSynthesizer.AllVoices.First(v => v.Language.StartsWith("en"));
        synth.Voice = voice;
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        using var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms);
        ms.Position = 0;
        using var reader = new WaveFileReader(ms);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1) provider = provider.ToMono();
        if (provider.WaveFormat.SampleRate != 16000) provider = new WdlResamplingSampleProvider(provider, 16000);
        var all = new List<float>();
        var buf = new float[16000];
        int n;
        while ((n = provider.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
        // Pad with silence like a real utterance.
        return [.. new float[8000], .. all, .. new float[8000]];
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}

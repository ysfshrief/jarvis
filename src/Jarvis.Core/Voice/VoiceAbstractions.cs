using System.Text.Json.Serialization;
using Jarvis.Core.Language;

namespace Jarvis.Core.Voice;

public sealed record VoiceInfo(string Id, string Name, string Language, string? Gender);

/// <summary>Speech output. Implementations: Windows OneCore/SAPI voices today; Piper or cloud voices later.</summary>
public interface ITextToSpeech
{
    string EngineName { get; }
    bool IsAvailable { get; }
    IReadOnlyList<VoiceInfo> Voices { get; }
    Task SpeakAsync(string text, Lang lang, CancellationToken ct);
    void Stop();
}

public sealed record Transcript(string Text, Lang? Language, double? Confidence, TimeSpan Duration);

/// <summary>Speech recognition over 16 kHz mono float samples. Implementations: Whisper (local) today.</summary>
public interface ISpeechToText
{
    string EngineName { get; }
    /// <summary>True when the engine and its model are installed and loaded.</summary>
    bool IsReady { get; }
    string? StatusMessage { get; }
    Task<Transcript> TranscribeAsync(float[] samples, Lang? hint, CancellationToken ct);
}

/// <summary>Microphone input producing 16 kHz mono float frames.</summary>
public interface IAudioInput
{
    bool IsAvailable { get; }
    string? DeviceName { get; }
    const int SampleRate = 16000;
    /// <summary>Raised on a background thread for every captured frame (about 30 ms).</summary>
    event Action<float[]>? FrameCaptured;
    void Start(int deviceIndex);
    void Stop();
    bool IsCapturing { get; }
}

[JsonConverter(typeof(JsonStringEnumConverter<VoiceState>))]
public enum VoiceState
{
    /// <summary>Voice is not available (no microphone or no speech model).</summary>
    Unavailable,
    Idle,
    /// <summary>Microphone open, waiting for the wake word.</summary>
    WakeListening,
    /// <summary>Recording a command.</summary>
    Listening,
    Transcribing,
    Thinking,
    Speaking,
    Paused,
}

public sealed class NullTextToSpeech : ITextToSpeech
{
    public string EngineName => "none";
    public bool IsAvailable => false;
    public IReadOnlyList<VoiceInfo> Voices => [];
    public Task SpeakAsync(string text, Lang lang, CancellationToken ct) => Task.CompletedTask;
    public void Stop() { }
}

public sealed class NullAudioInput : IAudioInput
{
    public bool IsAvailable => false;
    public string? DeviceName => null;
    public bool IsCapturing => false;
    public event Action<float[]>? FrameCaptured { add { } remove { } }
    public void Start(int deviceIndex) => throw new InvalidOperationException("No microphone support on this platform.");
    public void Stop() { }
}

/// <summary>Used when no speech engine is installed (e.g. tests or a build without voice).</summary>
public sealed class NullSpeechToText : ISpeechToText
{
    public string EngineName => "none";
    public bool IsReady => false;
    public string? StatusMessage => "Speech recognition isn't installed in this build.";
    public Task<Transcript> TranscribeAsync(float[] samples, Language.Lang? hint, CancellationToken ct) =>
        throw new InvalidOperationException(StatusMessage);
}

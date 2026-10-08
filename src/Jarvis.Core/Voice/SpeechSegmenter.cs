namespace Jarvis.Core.Voice;

/// <summary>
/// Energy-based voice activity detection that cuts a continuous microphone stream into
/// utterances. It tracks the background noise floor so it adapts to quiet rooms and noisy
/// ones. Pure logic (no audio APIs) so it is unit-testable.
/// </summary>
public sealed class SpeechSegmenter
{
    private readonly int _sampleRate;
    private readonly double _sensitivity;
    private readonly int _minSpeechSamples;
    private readonly int _endSilenceSamples;
    private readonly int _maxSamples;
    private readonly int _preRollSamples;

    private readonly List<float> _buffer = new();
    private readonly Queue<float[]> _preRoll = new();
    private int _preRollCount;
    private double _noiseFloor = 0.004;
    private bool _inSpeech;
    private int _silenceRun;
    private int _speechSamples;

    public SpeechSegmenter(int sampleRate = 16000, double sensitivity = 3.0, double minSpeechSeconds = 0.25,
        double endSilenceSeconds = 0.8, double maxSeconds = 15, double preRollSeconds = 0.3)
    {
        _sampleRate = sampleRate;
        _sensitivity = sensitivity;
        _minSpeechSamples = (int)(minSpeechSeconds * sampleRate);
        _endSilenceSamples = (int)(endSilenceSeconds * sampleRate);
        _maxSamples = (int)(maxSeconds * sampleRate);
        _preRollSamples = (int)(preRollSeconds * sampleRate);
    }

    public bool InSpeech => _inSpeech;
    public double NoiseFloor => _noiseFloor;
    /// <summary>Last frame level relative to the speech threshold (0..1+), for UI meters.</summary>
    public double Level { get; private set; }

    /// <summary>Raised when the segmenter detects the start of speech.</summary>
    public event Action? SpeechStarted;

    /// <summary>
    /// Feed one frame. Returns a finished utterance when speech has ended (or hit the maximum length);
    /// otherwise null. Utterances shorter than the minimum are discarded as noise.
    /// </summary>
    public float[]? Process(ReadOnlySpan<float> frame)
    {
        var rms = Rms(frame);
        var threshold = Math.Max(_noiseFloor * _sensitivity, 0.01);
        Level = rms / threshold;
        var isSpeech = rms > threshold;

        if (!_inSpeech)
        {
            // Adapt the noise floor only while nobody is talking.
            _noiseFloor = _noiseFloor * 0.95 + rms * 0.05;
            PushPreRoll(frame);
            if (isSpeech)
            {
                _inSpeech = true;
                _silenceRun = 0;
                _speechSamples = 0;
                _buffer.Clear();
                foreach (var f in _preRoll) _buffer.AddRange(f);
                _preRoll.Clear();
                _preRollCount = 0;
                SpeechStarted?.Invoke();
            }
            return null;
        }

        foreach (var s in frame) _buffer.Add(s);
        if (isSpeech)
        {
            _silenceRun = 0;
            _speechSamples += frame.Length;
        }
        else
        {
            _silenceRun += frame.Length;
        }

        if (_silenceRun >= _endSilenceSamples || _buffer.Count >= _maxSamples)
        {
            _inSpeech = false;
            var result = _speechSamples >= _minSpeechSamples ? _buffer.ToArray() : null;
            _buffer.Clear();
            return result;
        }
        return null;
    }

    /// <summary>Force-finish the current utterance (push-to-talk released).</summary>
    public float[]? Flush()
    {
        if (!_inSpeech || _buffer.Count == 0) { Reset(); return null; }
        var result = _speechSamples >= _minSpeechSamples ? _buffer.ToArray() : null;
        Reset();
        return result;
    }

    public void Reset()
    {
        _inSpeech = false;
        _buffer.Clear();
        _preRoll.Clear();
        _preRollCount = 0;
        _silenceRun = 0;
        _speechSamples = 0;
    }

    private void PushPreRoll(ReadOnlySpan<float> frame)
    {
        _preRoll.Enqueue(frame.ToArray());
        _preRollCount += frame.Length;
        while (_preRollCount > _preRollSamples && _preRoll.Count > 1)
            _preRollCount -= _preRoll.Dequeue().Length;
    }

    public static double Rms(ReadOnlySpan<float> frame)
    {
        if (frame.IsEmpty) return 0;
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        return Math.Sqrt(sum / frame.Length);
    }
}

/// <summary>Finds the wake word in a transcript and returns the command that follows it, if any.</summary>
public static class WakeWordMatcher
{
    // Whisper spells "Jarvis" in many ways, especially for Arabic speakers.
    private static readonly string[] Variants =
    [
        "jarvis", "jervis", "jarvys", "jarves", "jarvas", "javis", "gervis", "service jarvis",
        "جارفيس", "جارڤيس", "جرفيس", "جارفس", "جارفيز", "جارفيث", "جافيس", "جار فيس",
    ];

    public static bool TryMatch(string transcript, IEnumerable<string> configuredWakeWords, out string command)
    {
        command = "";
        var text = Language.TextNormalizer.Normalize(transcript);
        var words = configuredWakeWords.Select(Language.TextNormalizer.Normalize).Concat(Variants).Distinct()
            .OrderByDescending(w => w.Length);
        foreach (var w in words)
        {
            var idx = text.IndexOf(w, StringComparison.Ordinal);
            if (idx < 0) continue;
            // Only accept the wake word near the start ("Hey Jarvis, ..."), not mid-sentence chatter.
            if (idx > 12) continue;
            command = text[(idx + w.Length)..].Trim(' ', ',', '.', '!', '?', '،', ':', '-');
            return true;
        }
        return false;
    }
}

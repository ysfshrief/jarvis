using Jarvis.Core.Language;
using Jarvis.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Voice;

/// <summary>Speaks notifications the notification center decided deserve a voice interruption.</summary>
public sealed class VoiceNotificationSink(IServiceProvider services) : INotificationSink
{
    public string Name => "voice";

    public Task DeliverAsync(Notification n, DeliveryMode mode, CancellationToken ct)
    {
        if (!mode.HasFlag(DeliveryMode.Spoken)) return Task.CompletedTask;
        // Resolved lazily: the voice service depends on the agent, which depends on notifications.
        var voice = services.GetService<VoiceService>();
        if (voice is null) return Task.CompletedTask;
        var lang = n.Lang == "ar" ? Lang.Ar : LanguageDetector.Detect(n.Speech ?? n.Title);
        return voice.SpeakAsync(n.Speech ?? n.Title, lang, ct);
    }
}

public static class VoiceServices
{
    public static IServiceCollection AddJarvisVoice(this IServiceCollection services)
    {
        services.AddSingleton<SpeechModelManager>();
        services.AddSingleton<WhisperSpeechToText>();
        services.AddSingleton<Core.Voice.ISpeechToText>(sp => sp.GetRequiredService<WhisperSpeechToText>());
        services.AddSingleton<VoiceService>();
        services.AddHostedService(sp => sp.GetRequiredService<VoiceService>());
        services.AddSingleton<INotificationSink, VoiceNotificationSink>();
        return services;
    }
}

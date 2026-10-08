using Jarvis.Core.Activity;
using Jarvis.Core.Agent;
using Jarvis.Core.AI;
using Jarvis.Core.Connectivity;
using Jarvis.Core.Events;
using Jarvis.Core.Memory;
using Jarvis.Core.Notifications;
using Jarvis.Core.Permissions;
using Jarvis.Core.Persistence;
using Jarvis.Core.Presence;
using Jarvis.Core.Scheduling;
using Jarvis.Core.Security;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;
using Jarvis.Core.Tools;
using Jarvis.Core.Tools.Builtin;
using Jarvis.Core.Voice;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Core;

public static class CoreServices
{
    /// <summary>
    /// Registers the platform-neutral core. Platform layers register afterwards and may replace
    /// defaults (secret protector, presence, voice, trash, tools with the same name).
    /// </summary>
    public static IServiceCollection AddJarvisCore(this IServiceCollection services, JarvisPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IEventBus, EventBus>();
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<JarvisDatabase>();
        services.TryAddSingleton<ISecretProtector, KeyFileProtector>();
        services.AddSingleton<ISecretStore, FileSecretStore>();

        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
        })
        { Timeout = TimeSpan.FromMinutes(10) });

        services.AddSingleton<ActivityLog>();
        services.AddSingleton<MemoryStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<ReminderStore>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<OfflineQueue>();
        services.AddSingleton<ConnectivityMonitor>(sp => new ConnectivityMonitor(
            sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConnectivityMonitor>>()));
        services.AddSingleton<IConnectivity>(sp => sp.GetRequiredService<ConnectivityMonitor>());

        services.TryAddSingleton<IPresenceProvider, NullPresenceProvider>();
        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<ReminderDispatcher>();

        services.AddSingleton<PermissionService>();
        services.AddSingleton<ApprovalBroker>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<ModelManager>();
        services.AddSingleton<Monitoring.SystemMetrics>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<ToolExecutor>();
        services.AddSingleton<AgentOrchestrator>();

        services.TryAddSingleton<ITextToSpeech, NullTextToSpeech>();
        services.TryAddSingleton<IAudioInput, NullAudioInput>();
        services.TryAddSingleton<IFileTrash, FolderTrash>();
        services.TryAddSingleton(new PlatformInfo("generic", Environment.OSVersion.ToString()));

        services.AddSingleton<FilePolicy>();
        services.AddSingleton<ProjectLocator>();
        AddTool<MemoryRememberTool>(services);
        AddTool<MemorySearchTool>(services);
        AddTool<MemoryForgetTool>(services);
        AddTool<TaskCreateTool>(services);
        AddTool<TaskListTool>(services);
        AddTool<TaskCompleteTool>(services);
        AddTool<TaskUpdateTool>(services);
        AddTool<ReminderCreateTool>(services);
        AddTool<ReminderListTool>(services);
        AddTool<ReminderCancelTool>(services);
        AddTool<FileSearchTool>(services);
        AddTool<FileListTool>(services);
        AddTool<FileReadTool>(services);
        AddTool<FileWriteTool>(services);
        AddTool<FileMoveTool>(services);
        AddTool<FileDeleteTool>(services);
        AddTool<RunCommandTool>(services);
        AddTool<ProjectFindTool>(services);
        AddTool<ProjectOpenTool>(services);
        AddTool<ProjectBuildTool>(services);
        AddTool<SystemInfoTool>(services);
        AddTool<OpenUrlTool>(services);
        AddTool<WebSearchTool>(services);
        AddTool<WebReadTool>(services);
        return services;
    }

    /// <summary>Registers a tool so the registry (and therefore the agent) can use it.</summary>
    public static IServiceCollection AddTool<T>(this IServiceCollection services) where T : class, ITool
    {
        services.AddSingleton<T>();
        services.AddSingleton<ITool>(sp => sp.GetRequiredService<T>());
        return services;
    }
}

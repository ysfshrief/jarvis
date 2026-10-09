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
        services.AddSingleton<EntityStore>();
        services.AddSingleton<SemanticIndex>();
        services.AddSingleton<KnowledgeService>();
        services.AddSingleton<PatternLearner>();
        services.AddSingleton<Workflows.WorkflowStore>();
        services.AddSingleton<Workflows.WorkflowService>();
        services.AddSingleton<Workflows.WorkflowRunner>();
        services.TryAddSingleton<Files.IOcrEngine, Files.NullOcrEngine>();
        services.AddSingleton<Files.DocumentExtractor>();
        services.AddSingleton<Files.FileIndex>();
        services.AddSingleton<Files.FileIndexer>();
        services.AddSingleton<FileFinder>();
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
        services.TryAddSingleton<ISpeechToText, NullSpeechToText>();
        services.TryAddSingleton<Meetings.IMeetingAudioSource, Meetings.NullMeetingAudioSource>();
        services.TryAddSingleton<Vision.IScreenCapture, Vision.NullScreenCapture>();
        services.TryAddSingleton<Vision.ICamera, Vision.NullCamera>();
        services.TryAddSingleton<IFileTrash, FolderTrash>();
        services.TryAddSingleton(new PlatformInfo("generic", Environment.OSVersion.ToString()));

        services.AddSingleton<FilePolicy>();
        services.AddSingleton<ProjectLocator>();
        AddTool<MemoryRememberTool>(services);
        AddTool<MemorySearchTool>(services);
        AddTool<MemoryForgetTool>(services);
        AddTool<MemoryRelateTool>(services);
        AddTool<TaskCreateTool>(services);
        AddTool<TaskListTool>(services);
        AddTool<TaskCompleteTool>(services);
        AddTool<TaskUpdateTool>(services);
        AddTool<BriefingTool>(services);
        AddTool<WorkflowCreateTool>(services);
        AddTool<WorkflowStatusTool>(services);
        AddTool<WorkflowStepTool>(services);
        AddTool<WorkflowAddStepTool>(services);
        AddTool<WorkflowCancelTool>(services);
        AddTool<ReminderCreateTool>(services);
        AddTool<ReminderListTool>(services);
        AddTool<ReminderCancelTool>(services);
        AddTool<FileSearchTool>(services);
        AddTool<FileListTool>(services);
        AddTool<FileReadTool>(services);
        AddTool<FileWriteTool>(services);
        AddTool<FileMoveTool>(services);
        AddTool<FileDeleteTool>(services);
        AddTool<FileFindTool>(services);
        AddTool<FileLatestTool>(services);
        AddTool<FileExtractTool>(services);
        AddTool<FileSummarizeTool>(services);
        AddTool<FileCompareTool>(services);
        AddTool<RunCommandTool>(services);
        AddTool<ProjectFindTool>(services);
        AddTool<ProjectOpenTool>(services);
        AddTool<ProjectBuildTool>(services);
        AddTool<SystemInfoTool>(services);
        AddTool<OpenUrlTool>(services);
        AddTool<WebSearchTool>(services);
        AddTool<WebReadTool>(services);
        services.AddSingleton<Web.BrowserService>();
        services.AddSingleton<Companion.DeviceStore>();
        services.AddSingleton<Plugins.PluginSandbox>();
        services.AddSingleton<Plugins.PluginManager>();
        services.AddSingleton<Plugins.PluginGenerator>();
        AddTool<Plugins.PluginCreateTool>(services);
        AddTool<Plugins.PluginInstallTool>(services);
        AddTool<Plugins.PluginUpdateTool>(services);
        AddTool<Plugins.PluginListTool>(services);
        services.AddSingleton<Learning.IResearchSources, Learning.ToolResearchSources>();
        services.AddSingleton<Learning.WritingSamples>();
        services.AddSingleton<Learning.KnowledgeIngestion>();
        AddTool<Learning.ResearchTopicTool>(services);
        AddTool<Learning.LearnFromSourceTool>(services);
        services.AddSingleton<Learning.TopicWatch>();
        services.AddSingleton<Learning.ConversationDigest>();
        AddTool<Learning.TopicWatchTool>(services);
        AddTool<Learning.TopicUnwatchTool>(services);
        services.AddSingleton<Meetings.MeetingStore>();
        services.AddSingleton<Meetings.MeetingRecorder>();
        AddTool<Vision.ScreenDescribeTool>(services);
        AddTool<Vision.CameraLookTool>(services);
        AddTool<MeetingRecordStartTool>(services);
        AddTool<MeetingRecordStopTool>(services);
        AddTool<MeetingNotesTool>(services);
        services.AddSingleton<Agenda.AgendaStore>();
        services.AddSingleton<Agenda.CalendarService>();
        AddTool<CalendarAgendaTool>(services);
        AddTool<CalendarNextTool>(services);
        AddTool<CalendarAddTool>(services);
        AddTool<CalendarDeleteTool>(services);
        AddTool<MeetingPrepTool>(services);
        services.AddSingleton<Inbox.InboxStore>();
        services.AddSingleton<Inbox.IMailConnector, Inbox.ImapSmtpConnector>();
        services.AddSingleton<Inbox.InboxService>();
        AddTool<InboxCheckTool>(services);
        AddTool<InboxListTool>(services);
        AddTool<InboxReadTool>(services);
        AddTool<InboxDraftTool>(services);
        AddTool<InboxSendTool>(services);
        AddTool<InboxCategorizeTool>(services);
        AddTool<BrowserOpenTool>(services);
        AddTool<BrowserReadTool>(services);
        AddTool<BrowserClickTool>(services);
        AddTool<BrowserTypeTool>(services);
        AddTool<BrowserBackTool>(services);
        AddTool<BrowserScreenshotTool>(services);
        AddTool<BrowserCloseTool>(services);
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

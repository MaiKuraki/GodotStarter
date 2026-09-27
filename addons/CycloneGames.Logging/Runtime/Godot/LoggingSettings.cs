using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// How the pipeline drains its queue.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Automatic"/> resolves to <see cref="Threaded"/> on desktop and console, and to
/// <see cref="SingleThreaded"/> on the web, where there are no threads to run a pipeline worker on.
/// </para>
/// <para>
/// <b>Why this enum is not nested inside <see cref="LoggingSettings"/>.</b> It was, and that did
/// not compile: a nested type named <c>ExecutionMode</c> and an exported property named
/// <c>ExecutionMode</c> both introduce that name into the containing type, so the pair is CS0102 —
/// a nested type and a member may not share a name. Namespace scope also matches every other enum this
/// layer and the kernel use (<see cref="LogOutputMode"/>, <c>LogSeverity</c>,
/// <c>LogQueueOverflowPolicy</c>, <c>LogCategoryFilterMode</c>, <c>FileMaintenanceMode</c>,
/// <c>LogSourcePathMode</c>), all of which are namespace-scope.
/// </para>
/// </remarks>
public enum LoggingExecutionMode : byte
{
    Automatic = 0,
    Threaded = 1,
    SingleThreaded = 2
}

/// <summary>
/// Authoring surface for the CycloneGames logging backend, mirroring the Unity
/// <c>LoggingSettings</c> ScriptableObject.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two configuration sources, in priority order.</b> The primary source is
/// <i>Project Settings</i> — the plugin registers a <c>cyclone_games_logging/*</c> section
/// with sensible defaults, so a project is configured with zero assets and zero code. This
/// resource is the override seam: pass one to
/// <see cref="LoggingBootstrap.Initialize"/> for tooling, tests, per-mode overrides, or
/// a config shipped as a <c>.tres</c> through your own asset pipeline. See
/// <see cref="LoggingProjectSettings"/>.
/// </para>
/// <para>
/// <b>Why a Resource and not a plain C# class.</b> A Godot <see cref="Resource"/> gets the
/// inspector, <c>.tres</c> serialization, and <see cref="ResourceLoader"/> for free, which is
/// the exact role <c>ScriptableObject</c> plus <c>Resources.Load</c> plays in the Unity
/// package. Unlike UnityEngine's version, no runtime state is ever written back into this
/// asset: the asset is a pure input, and the composition root copies the values it needs.
/// </para>
/// <para>
/// <b>Every field is validated at bootstrap, never here.</b> Assigning an out-of-range value
/// in the inspector must not throw inside the editor's property setter, so validation happens
/// once in <see cref="LoggingBootstrap"/> through the kernel's own
/// <c>CreateValidated</c> entry points. That keeps the "one place validates" rule the Unity
/// package already follows.
/// </para>
/// </remarks>
[GlobalClass]
[Tool]
public partial class LoggingSettings : Resource
{
    // ---- Processing ----

    [ExportGroup("Processing")]
    [Export] public LoggingExecutionMode ExecutionMode { get; set; } = LoggingExecutionMode.Automatic;

    [Export] public int MaxQueuedMessages { get; set; } = LogPipelineOptions.DefaultMaxQueuedMessages;
    [Export] public int MaxQueuedCharacters { get; set; } = LogPipelineOptions.DefaultMaxQueuedCharacters;
    [Export] public int MaxMessageCharacters { get; set; } = LogPipelineOptions.DefaultMaxMessageCharacters;
    [Export] public int MaxCategoryCharacters { get; set; } = LogPipelineOptions.DefaultMaxCategoryCharacters;
    [Export] public int MaxSourcePathCharacters { get; set; } = LogPipelineOptions.DefaultMaxSourcePathCharacters;
    [Export] public int MaxMemberNameCharacters { get; set; } = LogPipelineOptions.DefaultMaxMemberNameCharacters;
    [Export] public int MaxFilterCategories { get; set; } = LogPipelineOptions.DefaultMaxFilterCategories;
    [Export] public int MaxFilterCharacters { get; set; } = LogPipelineOptions.DefaultMaxFilterCharacters;
    [Export] public int ReservedCriticalMessages { get; set; } = LogPipelineOptions.DefaultReservedCriticalMessages;
    [Export] public int ReservedCriticalCharacters { get; set; } = LogPipelineOptions.DefaultReservedCriticalCharacters;

    [ExportGroup("Handoff")]
    [Export] public int HandoffMaxQueuedMessages { get; set; } = GodotConsoleLogSinkOptions.DefaultMaxQueuedMessages;
    [Export] public int HandoffMaxQueuedCharacters { get; set; } = GodotConsoleLogSinkOptions.DefaultMaxQueuedCharacters;
    [Export] public LogQueueOverflowPolicy HandoffOverflowPolicy { get; set; } = LogQueueOverflowPolicy.DropNewest;

    [ExportGroup("Lifecycle")]
    [Export] public int ShutdownDrainTimeoutMs { get; set; } = LogPipelineOptions.DefaultShutdownDrainTimeoutMs;
    [Export] public int EnqueueBlockTimeoutMs { get; set; } = 1;
    [Export] public int MaintenanceIntervalMs { get; set; } = LogPipelineOptions.DefaultMaintenanceIntervalMs;
    [Export] public int SinkFailureThreshold { get; set; } = LogPipelineOptions.DefaultSinkFailureThreshold;
    [Export] public LogQueueOverflowPolicy OverflowPolicy { get; set; } = LogQueueOverflowPolicy.DropNewest;

    /// <summary>
    /// Severity that may use reserved queue capacity. This is <b>not</b> an absolute delivery
    /// guarantee — no finite queue can promise delivery under unbounded overload.
    /// </summary>
    /// <remarks>
    /// No <c>[Tooltip]</c> here, deliberately: that attribute is Unity's
    /// (<c>UnityEngine.TooltipAttribute</c>) and does not exist in Godot. GodotSharp contains no
    /// inspector-tooltip attribute at all — its tooltip surface is runtime <c>Control</c> /
    /// <c>ItemList</c> methods. The editor takes an exported member's tooltip from this XML
    /// <c>&lt;summary&gt;</c>, so the text above is already doing the work an attribute would.
    /// </remarks>
    [Export]
    public LogSeverity CriticalSeverity { get; set; } = LogSeverity.Error;

    // ---- Sink registration ----

    [ExportGroup("Registration")]
    [Export] public bool RegisterGodotConsoleSink { get; set; } = true;
    [Export] public bool RegisterConsoleLogSink { get; set; }
    [Export] public bool RegisterFileLogSink { get; set; }

    // ---- File sink ----

    [ExportGroup("File Sink")]
    [Export] public bool UseUserDataPath { get; set; } = true;

    [Export] public string FileName { get; set; } = "App.log";
    [Export] public bool AllowCustomFilePath { get; set; }
    [Export] public string CustomFilePath { get; set; } = string.Empty;
    [Export] public FileMaintenanceMode FileMaintenanceMode { get; set; } = FileMaintenanceMode.Rotate;
    [Export] public long MaxFileBytes { get; set; } = 10L * 1024L * 1024L;
    [Export] public int MaxArchiveFiles { get; set; } = 5;
    [Export] public int FileFlushBatchSize { get; set; } = 64;
    [Export] public int FileFlushIntervalMs { get; set; } = 1000;
    [Export] public bool DurableFlushOnFatal { get; set; }
    [Export] public LogSourcePathMode FileSourcePathMode { get; set; } = LogSourcePathMode.FileName;

    // ---- Filtering ----

    [ExportGroup("Filtering")]
    [Export] public LogSeverity MinimumSeverity { get; set; } = LogSeverity.Info;
    [Export] public LogCategoryFilterMode CategoryFilter { get; set; } = LogCategoryFilterMode.All;

    // ---- Output ----

    [ExportGroup("Output")]
    /// <summary>
    /// How records reach Godot's output. Defaults to <see cref="LogOutputMode.StreamOnly"/>;
    /// see <see cref="LogOutputMode"/> for why, and switch to
    /// <see cref="LogOutputMode.EngineDiagnostics"/> for a validation or soak run whose pass/fail
    /// criterion is "no engine errors".
    /// </summary>
    [Export] public LogOutputMode OutputMode { get; set; } = LogOutputMode.StreamOnly;

    /// <summary>
    /// Pads the severity tag to a fixed column so the <c>[Category]</c> column lines up across
    /// levels. Off by default: the line is a documented, parsed format rather than decoration, and
    /// padding aligns the category column but not the message column, because category names are
    /// caller-defined and unbounded. See <see cref="GodotConsoleLogSinkOptions.PadSeverity"/> for the
    /// full argument, including why the padding is placed after the colon.
    /// </summary>
    [Export] public bool PadSeverity { get; set; } = false;

    /// <summary>
    /// Per-frame item budget for the main-thread handoff pump. One frame never pays an
    /// unbounded drain, which is what keeps a burst from turning into a frame-time spike.
    /// </summary>
    [Export] public int PumpItemsPerFrame { get; set; } = LoggingRuntimeHost.DefaultPumpItems;

    /// <summary>
    /// Per-frame millisecond budget for the handoff pump. Zero disables the time bound and
    /// leaves only <see cref="PumpItemsPerFrame"/> in force.
    /// </summary>
    [Export] public double PumpBudgetMs { get; set; } = LoggingRuntimeHost.DefaultPumpBudgetMs;

    /// <summary>
    /// Per-frame millisecond budget for draining the pipeline into its sinks. Only meaningful
    /// for the single-threaded execution mode; the threaded mode has its own worker.
    /// </summary>
    [Export] public double PipelinePumpBudgetMs { get; set; } = LoggingRuntimeHost.DefaultPipelinePumpBudgetMs;

    public LoggingSettings()
    {
    }

    /// <summary>Returns an independent copy so a caller cannot mutate a shared resource.</summary>
    public LoggingSettings Clone()
    {
        return (LoggingSettings)Duplicate();
    }

    /// <summary>
    /// A settings instance built purely from <i>Project Settings</i>. Used when
    /// <see cref="LoggingBootstrap.Initialize"/> is called with a null argument, which is
    /// the zero-configuration path.
    /// </summary>
    public static LoggingSettings FromProjectSettings()
    {
        return LoggingProjectSettings.BuildSettings();
    }
}

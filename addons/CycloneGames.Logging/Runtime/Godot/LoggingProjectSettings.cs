using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// The <i>Project Settings</i> bridge: key names, registration, and reading.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why Project Settings is the primary configuration source.</b> The Unity package ships a
/// <c>LoggingSettings</c> ScriptableObject that has to be created by hand and placed in a
/// <c>Resources</c> folder before anything can be configured. Godot offers a strictly better
/// option for build-time configuration: the editor already renders a UI, already serializes
/// values into <c>project.godot</c>, and already handles overrides per export preset through
/// <c>ProjectSettings</c> feature tags. The plugin registers its own section with working
/// defaults on enable, so a project is configured by opening Project Settings — no asset to
/// create, no path to remember, nothing to accidentally delete.
/// </para>
/// <para>
/// <b>Registration is editor-only.</b> <see cref="RegisterDefaults"/> runs from the plugin's
/// editor tooling and never exists in an exported game. Values written by the user live in
/// <c>project.godot</c> and are therefore present in the runtime export, which is why reading
/// (<see cref="BuildSettings"/>) is a plain runtime operation with no editor dependency.
/// </para>
/// <para>
/// <b>Reads are cheap and allocation-free.</b> Every key is fetched with an explicit default,
/// so a project that never touched the plugin's settings still produces a fully valid
/// configuration. A missing or hand-edited key can never fail the boot: values are re-validated
/// by the kernel's <c>CreateValidated</c> at bootstrap, and an invalid value surfaces as one
/// clear error rather than a half-initialized backend.
/// </para>
/// </remarks>
public static class LoggingProjectSettings
{
    /// <summary>Root section name shown in Project Settings.</summary>
    public const string Section = "cyclone_games_logging";

    // ---- Key names ----
    public const string KeyExecutionMode = Section + "/execution_mode";
    public const string KeyMaxQueuedMessages = Section + "/pipeline/max_queued_messages";
    public const string KeyMaxQueuedCharacters = Section + "/pipeline/max_queued_characters";
    public const string KeyMaxMessageCharacters = Section + "/pipeline/max_message_characters";
    public const string KeyMaxCategoryCharacters = Section + "/pipeline/max_category_characters";
    public const string KeyMaxSourcePathCharacters = Section + "/pipeline/max_source_path_characters";
    public const string KeyMaxMemberNameCharacters = Section + "/pipeline/max_member_name_characters";
    public const string KeyMaxFilterCategories = Section + "/pipeline/max_filter_categories";
    public const string KeyMaxFilterCharacters = Section + "/pipeline/max_filter_characters";
    public const string KeyReservedCriticalMessages = Section + "/pipeline/reserved_critical_messages";
    public const string KeyReservedCriticalCharacters = Section + "/pipeline/reserved_critical_characters";
    public const string KeyOverflowPolicy = Section + "/pipeline/overflow_policy";
    public const string KeyCriticalSeverity = Section + "/pipeline/critical_severity";
    public const string KeyShutdownDrainTimeoutMs = Section + "/pipeline/shutdown_drain_timeout_ms";
    public const string KeyEnqueueBlockTimeoutMs = Section + "/pipeline/enqueue_block_timeout_ms";
    public const string KeyMaintenanceIntervalMs = Section + "/pipeline/maintenance_interval_ms";
    public const string KeySinkFailureThreshold = Section + "/pipeline/sink_failure_threshold";

    public const string KeyHandoffMaxQueuedMessages = Section + "/handoff/max_queued_messages";
    public const string KeyHandoffMaxQueuedCharacters = Section + "/handoff/max_queued_characters";
    public const string KeyHandoffOverflowPolicy = Section + "/handoff/overflow_policy";

    public const string KeyRegisterGodotConsoleSink = Section + "/registration/godot_console_sink";
    public const string KeyRegisterConsoleLogSink = Section + "/registration/console_log_sink";
    public const string KeyRegisterFileLogSink = Section + "/registration/file_log_sink";

    /// <summary>
    /// Controls whether the plugin registers <c>CycloneGamesLoggingHost</c> as an autoload.
    /// </summary>
    /// <remarks>
    /// Read by the GDScript plugin shim before anything else is registered, which is why the shim
    /// supplies the same <c>true</c> default when the key is absent — the shim runs before this
    /// class has ever registered it. See the shim's <c>_register_autoload</c> for why the autoload is
    /// added but never removed.
    /// </remarks>
    public const string KeyRegisterAutoload = Section + "/registration/register_autoload";

    public const string KeyUseUserDataPath = Section + "/file/use_user_data_path";
    public const string KeyFileName = Section + "/file/file_name";
    public const string KeyAllowCustomFilePath = Section + "/file/allow_custom_file_path";
    public const string KeyCustomFilePath = Section + "/file/custom_file_path";
    public const string KeyFileMaintenanceMode = Section + "/file/maintenance_mode";
    public const string KeyMaxFileBytes = Section + "/file/max_file_bytes";
    public const string KeyMaxArchiveFiles = Section + "/file/max_archive_files";
    public const string KeyFileFlushBatchSize = Section + "/file/flush_batch_size";
    public const string KeyFileFlushIntervalMs = Section + "/file/flush_interval_ms";
    public const string KeyDurableFlushOnFatal = Section + "/file/durable_flush_on_fatal";
    public const string KeyFileSourcePathMode = Section + "/file/source_path_mode";

    public const string KeyMinimumSeverity = Section + "/filtering/minimum_severity";
    public const string KeyCategoryFilter = Section + "/filtering/category_filter";

    public const string KeyOutputMode = Section + "/output/mode";
    public const string KeyPumpItemsPerFrame = Section + "/output/pump_items_per_frame";
    public const string KeyPumpBudgetMs = Section + "/output/pump_budget_ms";
    public const string KeyPipelinePumpBudgetMs = Section + "/output/pipeline_pump_budget_ms";

    /// <summary>
    /// <c>PROPERTY_USAGE_DEFAULT</c> = <c>STORAGE (2) | EDITOR (4)</c>. Spelled numerically
    /// rather than through a managed enum so the registration compiles unchanged across
    /// Godot 4.x, where the usage-flag enum's managed name has moved between releases.
    /// A wrong value here would only affect inspector visibility, never runtime behaviour,
    /// because <see cref="BuildSettings"/> reads with explicit defaults.
    /// </summary>
    // There is deliberately no property-usage constant here. Godot 4.7 rejects a "usage" key in the
    // dictionary handed to ProjectSettings.AddPropertyInfo — it reports
    //   WARNING: "usage" is not supported in add_property_info().
    // — and reads only name / type / hint / hint_string. An earlier revision passed a numerically
    // spelled PROPERTY_USAGE_DEFAULT in the belief that a managed enum name might have moved between
    // releases; the value never took effect and the warning was the only symptom. Godot applies its own
    // usage flags, so the key is simply gone.

    private static readonly string[] ExecutionModeNames = { "Automatic", "Threaded", "SingleThreaded" };
    private static readonly string[] OverflowPolicyNames = { "DropNewest", "DropOldest", "Block" };
    private static readonly string[] HandoffOverflowPolicyNames = { "DropNewest", "DropOldest" };
    private static readonly string[] MaintenanceModeNames = { "None", "WarnOnly", "Rotate" };
    private static readonly string[] SourcePathModeNames = { "FileName", "None", "FullPath" };
    private static readonly string[] SeverityNames = { "Trace", "Debug", "Info", "Warning", "Error", "Fatal", "None" };
    private static readonly string[] CategoryFilterNames = { "All", "AllowList", "DenyList" };
    private static readonly string[] OutputModeNames = { "StreamOnly", "EngineDiagnostics" };

    /// <summary>
    /// Registers the section and every default into <see cref="ProjectSettings"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent: a setting that already exists is left alone, so a user's edited value survives a
    /// plugin reload or an addon upgrade. Newly introduced keys are added on the next run. See
    /// <see cref="RegisterSetting"/> for why the call order inside each registration is the part that
    /// actually matters.
    /// </para>
    /// <para>
    /// Never throws. A settings registration failure must not be able to break the editor, and
    /// the whole section is optional anyway: the pipeline carries its own defaults, so a
    /// project with no registration runs correctly.
    /// </para>
    /// </remarks>
    public static void RegisterDefaults()
    {
        try
        {
            var defaults = new LoggingSettings();

            AddInt(KeyExecutionMode, (int)defaults.ExecutionMode, ExecutionModeNames);
            AddInt(KeyMaxQueuedMessages, defaults.MaxQueuedMessages);
            AddInt(KeyMaxQueuedCharacters, defaults.MaxQueuedCharacters);
            AddInt(KeyMaxMessageCharacters, defaults.MaxMessageCharacters);
            AddInt(KeyMaxCategoryCharacters, defaults.MaxCategoryCharacters);
            AddInt(KeyMaxSourcePathCharacters, defaults.MaxSourcePathCharacters);
            AddInt(KeyMaxMemberNameCharacters, defaults.MaxMemberNameCharacters);
            AddInt(KeyMaxFilterCategories, defaults.MaxFilterCategories);
            AddInt(KeyMaxFilterCharacters, defaults.MaxFilterCharacters);
            AddInt(KeyReservedCriticalMessages, defaults.ReservedCriticalMessages);
            AddInt(KeyReservedCriticalCharacters, defaults.ReservedCriticalCharacters);
            AddInt(KeyOverflowPolicy, (int)defaults.OverflowPolicy, OverflowPolicyNames);
            AddInt(KeyCriticalSeverity, (int)defaults.CriticalSeverity, SeverityNames);
            AddInt(KeyShutdownDrainTimeoutMs, defaults.ShutdownDrainTimeoutMs);
            AddInt(KeyEnqueueBlockTimeoutMs, defaults.EnqueueBlockTimeoutMs);
            AddInt(KeyMaintenanceIntervalMs, defaults.MaintenanceIntervalMs);
            AddInt(KeySinkFailureThreshold, defaults.SinkFailureThreshold);

            AddInt(KeyHandoffMaxQueuedMessages, defaults.HandoffMaxQueuedMessages);
            AddInt(KeyHandoffMaxQueuedCharacters, defaults.HandoffMaxQueuedCharacters);
            AddInt(KeyHandoffOverflowPolicy, (int)defaults.HandoffOverflowPolicy, HandoffOverflowPolicyNames);

            AddBool(KeyRegisterGodotConsoleSink, defaults.RegisterGodotConsoleSink);
            AddBool(KeyRegisterConsoleLogSink, defaults.RegisterConsoleLogSink);
            AddBool(KeyRegisterFileLogSink, defaults.RegisterFileLogSink);
            // Registered last in its group and defaulted to true so a fresh clone is
            // zero-configuration; the plugin shim reads it before this method ever runs, which is
            // why the shim also supplies the same default when the key is absent.
            AddBool(KeyRegisterAutoload, true);

            AddBool(KeyUseUserDataPath, defaults.UseUserDataPath);
            AddString(KeyFileName, defaults.FileName);
            AddBool(KeyAllowCustomFilePath, defaults.AllowCustomFilePath);
            AddString(KeyCustomFilePath, defaults.CustomFilePath);
            AddInt(KeyFileMaintenanceMode, (int)defaults.FileMaintenanceMode, MaintenanceModeNames);
            AddLong(KeyMaxFileBytes, defaults.MaxFileBytes);
            AddInt(KeyMaxArchiveFiles, defaults.MaxArchiveFiles);
            AddInt(KeyFileFlushBatchSize, defaults.FileFlushBatchSize);
            AddInt(KeyFileFlushIntervalMs, defaults.FileFlushIntervalMs);
            AddBool(KeyDurableFlushOnFatal, defaults.DurableFlushOnFatal);
            AddInt(KeyFileSourcePathMode, (int)defaults.FileSourcePathMode, SourcePathModeNames);

            AddInt(KeyMinimumSeverity, (int)defaults.MinimumSeverity, SeverityNames);
            AddInt(KeyCategoryFilter, (int)defaults.CategoryFilter, CategoryFilterNames);

            AddInt(KeyOutputMode, (int)defaults.OutputMode, OutputModeNames);
            AddInt(KeyPumpItemsPerFrame, defaults.PumpItemsPerFrame);
            AddFloat(KeyPumpBudgetMs, defaults.PumpBudgetMs);
            AddFloat(KeyPipelinePumpBudgetMs, defaults.PipelinePumpBudgetMs);
        }
        catch
        {
            // Registration is a convenience layer. The pipeline defaults already make the
            // backend fully functional without a single Project Settings key.
        }
    }

    /// <summary>
    /// Reads the whole section into a fresh settings instance. Called on the zero-configuration
    /// path, so it must tolerate every key being absent.
    /// </summary>
    public static LoggingSettings BuildSettings()
    {
        var settings = new LoggingSettings();
        var fallback = new LoggingSettings();

        settings.ExecutionMode = (LoggingExecutionMode)GetInt(
            KeyExecutionMode, (int)fallback.ExecutionMode);
        settings.MaxQueuedMessages = GetInt(KeyMaxQueuedMessages, fallback.MaxQueuedMessages);
        settings.MaxQueuedCharacters = GetInt(KeyMaxQueuedCharacters, fallback.MaxQueuedCharacters);
        settings.MaxMessageCharacters = GetInt(KeyMaxMessageCharacters, fallback.MaxMessageCharacters);
        settings.MaxCategoryCharacters = GetInt(KeyMaxCategoryCharacters, fallback.MaxCategoryCharacters);
        settings.MaxSourcePathCharacters = GetInt(KeyMaxSourcePathCharacters, fallback.MaxSourcePathCharacters);
        settings.MaxMemberNameCharacters = GetInt(KeyMaxMemberNameCharacters, fallback.MaxMemberNameCharacters);
        settings.MaxFilterCategories = GetInt(KeyMaxFilterCategories, fallback.MaxFilterCategories);
        settings.MaxFilterCharacters = GetInt(KeyMaxFilterCharacters, fallback.MaxFilterCharacters);
        settings.ReservedCriticalMessages = GetInt(KeyReservedCriticalMessages, fallback.ReservedCriticalMessages);
        settings.ReservedCriticalCharacters = GetInt(KeyReservedCriticalCharacters, fallback.ReservedCriticalCharacters);
        settings.OverflowPolicy = (LogQueueOverflowPolicy)GetInt(KeyOverflowPolicy, (int)fallback.OverflowPolicy);
        settings.CriticalSeverity = (LogSeverity)GetInt(KeyCriticalSeverity, (int)fallback.CriticalSeverity);
        settings.ShutdownDrainTimeoutMs = GetInt(KeyShutdownDrainTimeoutMs, fallback.ShutdownDrainTimeoutMs);
        settings.EnqueueBlockTimeoutMs = GetInt(KeyEnqueueBlockTimeoutMs, fallback.EnqueueBlockTimeoutMs);
        settings.MaintenanceIntervalMs = GetInt(KeyMaintenanceIntervalMs, fallback.MaintenanceIntervalMs);
        settings.SinkFailureThreshold = GetInt(KeySinkFailureThreshold, fallback.SinkFailureThreshold);

        settings.HandoffMaxQueuedMessages = GetInt(KeyHandoffMaxQueuedMessages, fallback.HandoffMaxQueuedMessages);
        settings.HandoffMaxQueuedCharacters = GetInt(KeyHandoffMaxQueuedCharacters, fallback.HandoffMaxQueuedCharacters);
        settings.HandoffOverflowPolicy = (LogQueueOverflowPolicy)GetInt(
            KeyHandoffOverflowPolicy, (int)fallback.HandoffOverflowPolicy);

        settings.RegisterGodotConsoleSink = GetBool(KeyRegisterGodotConsoleSink, fallback.RegisterGodotConsoleSink);
        settings.RegisterConsoleLogSink = GetBool(KeyRegisterConsoleLogSink, fallback.RegisterConsoleLogSink);
        settings.RegisterFileLogSink = GetBool(KeyRegisterFileLogSink, fallback.RegisterFileLogSink);

        settings.UseUserDataPath = GetBool(KeyUseUserDataPath, fallback.UseUserDataPath);
        settings.FileName = GetString(KeyFileName, fallback.FileName);
        settings.AllowCustomFilePath = GetBool(KeyAllowCustomFilePath, fallback.AllowCustomFilePath);
        settings.CustomFilePath = GetString(KeyCustomFilePath, fallback.CustomFilePath);
        settings.FileMaintenanceMode = (FileMaintenanceMode)GetInt(
            KeyFileMaintenanceMode, (int)fallback.FileMaintenanceMode);
        settings.MaxFileBytes = GetLong(KeyMaxFileBytes, fallback.MaxFileBytes);
        settings.MaxArchiveFiles = GetInt(KeyMaxArchiveFiles, fallback.MaxArchiveFiles);
        settings.FileFlushBatchSize = GetInt(KeyFileFlushBatchSize, fallback.FileFlushBatchSize);
        settings.FileFlushIntervalMs = GetInt(KeyFileFlushIntervalMs, fallback.FileFlushIntervalMs);
        settings.DurableFlushOnFatal = GetBool(KeyDurableFlushOnFatal, fallback.DurableFlushOnFatal);
        settings.FileSourcePathMode = (LogSourcePathMode)GetInt(
            KeyFileSourcePathMode, (int)fallback.FileSourcePathMode);

        settings.MinimumSeverity = (LogSeverity)GetInt(KeyMinimumSeverity, (int)fallback.MinimumSeverity);
        settings.CategoryFilter = (LogCategoryFilterMode)GetInt(KeyCategoryFilter, (int)fallback.CategoryFilter);

        settings.OutputMode = (LogOutputMode)GetInt(KeyOutputMode, (int)fallback.OutputMode);
        settings.PumpItemsPerFrame = GetInt(KeyPumpItemsPerFrame, fallback.PumpItemsPerFrame);
        settings.PumpBudgetMs = GetFloat(KeyPumpBudgetMs, fallback.PumpBudgetMs);
        settings.PipelinePumpBudgetMs = GetFloat(KeyPipelinePumpBudgetMs, fallback.PipelinePumpBudgetMs);

        return settings;
    }

    /// <summary>True when the section exists, i.e. the plugin registered it at least once.</summary>
    public static bool IsRegistered()
    {
        return ProjectSettings.HasSetting(KeyRegisterGodotConsoleSink);
    }

    // ---- Property registration helpers ----

    /// <summary>
    /// Registers one setting: creates it, records its default, and attaches its inspector hint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is load-bearing, and getting it wrong fails silently.</b> Godot keeps a property
    /// map, and both of the later calls require the setting to already be in it:
    /// </para>
    /// <list type="bullet">
    /// <item><c>SetInitialValue</c> on a setting that does not exist logs
    /// <c>Request for nonexistent project setting</c> and does nothing.</item>
    /// <item>A property info whose name is absent produces
    /// <c>ERROR: Condition "!props.has(pinfo.name)" is true.</c> and the setting never appears in the
    /// inspector at all.</item>
    /// </list>
    /// <para>
    /// An earlier revision called <c>AddPropertyInfo</c> first and <c>SetSetting</c> never, on the
    /// mistaken belief that <c>SetInitialValue</c> creates a missing setting. The entire
    /// <c>cyclone_games_logging/*</c> section was therefore absent from Project Settings while the
    /// addon otherwise looked healthy — the same "partial function hiding a missing piece" failure
    /// mode the plugin shim now escalates an error for. Compilation cannot catch either symptom; only
    /// running the editor shows it.
    /// </para>
    /// <para>
    /// <b>Idempotency.</b> The <c>HasSetting</c> guard is what makes a plugin reload safe: a value the
    /// user already edited is never overwritten. <c>SetInitialValue</c> is still called every time,
    /// because that is what supplies the "revert to default" value in the inspector and it does not
    /// touch the stored setting.
    /// </para>
    /// </remarks>
    private static void RegisterSetting(
        string name,
        Variant defaultValue,
        Variant.Type type,
        PropertyHint hint,
        string hintString)
    {
        if (!ProjectSettings.HasSetting(name))
        {
            ProjectSettings.SetSetting(name, defaultValue);
        }

        ProjectSettings.SetInitialValue(name, defaultValue);
        ProjectSettings.SetAsBasic(name, true);
        AddPropertyInfo(name, type, hint, hintString);
    }

    private static void AddInt(string name, int value, string[] enumNames = null)
    {
        RegisterSetting(
            name,
            value,
            Variant.Type.Int,
            enumNames == null ? PropertyHint.None : PropertyHint.Enum,
            enumNames == null ? string.Empty : string.Join(",", enumNames));
    }

    /// <summary>
    /// Registers a 64-bit integer setting.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddInt"/> only to keep the C# types honest: the file-size limit is a
    /// <c>long</c>, and Godot's <c>Variant.Int</c> is 64-bit anyway, so narrowing it to <c>int</c> just
    /// to share a helper would cap a configured maximum at ~2 GB and turn "raise the log file limit"
    /// into a silent truncation.
    /// </remarks>
    private static void AddLong(string name, long value)
    {
        RegisterSetting(name, value, Variant.Type.Int, PropertyHint.None, string.Empty);
    }

    private static void AddBool(string name, bool value)
    {
        RegisterSetting(name, value, Variant.Type.Bool, PropertyHint.None, string.Empty);
    }

    private static void AddString(string name, string value)
    {
        RegisterSetting(name, value, Variant.Type.String, PropertyHint.None, string.Empty);
    }

    private static void AddFloat(string name, double value)
    {
        RegisterSetting(name, value, Variant.Type.Float, PropertyHint.Range, "0,1000,0.05,or_greater");
    }

    private static void AddPropertyInfo(string name, Variant.Type type, PropertyHint hint, string hintString)
    {
        var info = new global::Godot.Collections.Dictionary
        {
            { "name", name },
            { "type", (int)type },
            { "hint", (int)hint },
            { "hint_string", hintString },
        };

        ProjectSettings.AddPropertyInfo(info);
    }

    // ---- Typed reads ----

    private static int GetInt(string name, int fallback)
    {
        Variant value = ProjectSettings.GetSetting(name, fallback);
        return value.VariantType == Variant.Type.Int ? value.AsInt32() : fallback;
    }

    /// <summary>
    /// Reads a 64-bit integer setting, clamped into the <c>long</c> range.
    /// </summary>
    /// <remarks>
    /// Reads through <c>AsInt64</c> rather than <c>AsInt32</c>: a limit stored above
    /// <c>int.MaxValue</c> would otherwise wrap or clamp to a value the user never typed, and a
    /// silently lowered file-size cap is the kind of defect that only shows up as a log file that
    /// rotates earlier than configured.
    /// </remarks>
    private static long GetLong(string name, long fallback)
    {
        Variant value = ProjectSettings.GetSetting(name, fallback);
        return value.VariantType == Variant.Type.Int ? value.AsInt64() : fallback;
    }

    private static bool GetBool(string name, bool fallback)
    {
        Variant value = ProjectSettings.GetSetting(name, fallback);
        return value.VariantType == Variant.Type.Bool ? value.AsBool() : fallback;
    }

    private static string GetString(string name, string fallback)
    {
        Variant value = ProjectSettings.GetSetting(name, fallback);
        return value.VariantType == Variant.Type.String ? value.AsString() : fallback;
    }

    private static double GetFloat(string name, double fallback)
    {
        Variant value = ProjectSettings.GetSetting(name, fallback);
        switch (value.VariantType)
        {
            case Variant.Type.Float:
                return value.AsDouble();
            case Variant.Type.Int:
                return value.AsInt32();
            default:
                return fallback;
        }
    }
}

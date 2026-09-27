using System;
using System.Threading;
using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// The composition root: owns the project's <see cref="LogPipeline"/>, its sinks, and the
/// process-wide writer installation.
/// </summary>
/// <remarks>
/// <para>
/// <b>How this replaces the Unity bootstrap.</b> The Unity package initializes itself from
/// <c>[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]</c> and reads a <c>Resources</c> asset.
/// Godot has neither construct, so this class is driven by
/// <see cref="LoggingRuntimeHost"/>'s autoload, and its configuration comes from Project Settings
/// (see <see cref="LoggingProjectSettings"/>) with an explicit
/// <see cref="LoggingSettings"/> argument as the override seam. Everything that made the
/// Unity version safe is preserved verbatim, because the hazards are identical:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Ownership is explicit.</b> <see cref="Initialize"/> installs the pipeline only through
/// <c>LogRuntime.TryInstallWriter</c>, which is a compare-and-swap. A process writer installed
/// by someone else is reported, never adopted and never replaced — so two systems cannot both
/// believe they own the writer.
/// </item>
/// <item>
/// <b>Initialization is transactional.</b> Every failure path after the pipeline is constructed
/// rolls the pipeline back through the same shutdown sequence used at exit. A half-built backend
/// that keeps a worker thread alive is worse than no backend at all.
/// </item>
/// <item>
/// <b>A failed shutdown keeps ownership.</b> If the pipeline will not drain, the bootstrap holds
/// the reference and refuses to re-initialize, so the only recovery is an explicit retry of
/// <see cref="Shutdown"/>. Silently dropping the reference would leak the worker and its file
/// handles for the rest of the process.
/// </item>
/// </list>
/// <para>
/// <b>All entry points are main-thread only.</b> They touch the scene tree (the host node) and
/// Project Settings, both of which are main-thread state.
/// </para>
/// </remarks>
public static class LoggingBootstrap
{
    private enum LifecycleState : byte
    {
        Stopped = 0,
        Running = 1,
        ShutdownIncomplete = 2
    }

    private static readonly object LifecycleLock = new object();
    private static LogPipeline _ownedPipeline;
    private static LogPipeline _installedProcessWriter;
    private static int _lifecycleState;

    /// <summary>
    /// Initializes the logging backend once. A null argument loads Project Settings and then
    /// falls back to package defaults, which is the zero-configuration path the plugin's autoload
    /// uses.
    /// </summary>
    public static LoggingInitializationResult Initialize(LoggingSettings settings = null)
    {
        LoggingThreading.AssertMainThread("LoggingBootstrap.Initialize");

        lock (LifecycleLock)
        {
            LifecycleState state = (LifecycleState)Volatile.Read(ref _lifecycleState);
            if (state == LifecycleState.ShutdownIncomplete)
            {
                LogPipeline installed = Volatile.Read(ref _installedProcessWriter);
                return new LoggingInitializationResult(
                    LoggingInitializationStatus.ShutdownFailed,
                    installed != null && ReferenceEquals(LogRuntime.Writer, installed));
            }

            if (state == LifecycleState.Running)
            {
                LogPipeline installed = Volatile.Read(ref _installedProcessWriter);
                return new LoggingInitializationResult(
                    LoggingInitializationStatus.AlreadyInitialized,
                    installed != null && ReferenceEquals(LogRuntime.Writer, installed));
            }

            if (LogRuntime.HasWriter)
            {
                return new LoggingInitializationResult(
                    LoggingInitializationStatus.ExistingProcessWriterNotOwned,
                    false);
            }

            return InitializeCore(settings ?? LoggingProjectSettings.BuildSettings());
        }
    }

    /// <summary>
    /// Drains and shuts down the owned pipeline, then applies the supplied settings.
    /// Re-initialization does not continue when the previous pipeline cannot stop safely.
    /// </summary>
    public static LoggingReinitializationResult Reinitialize(
        LoggingSettings settings = null,
        LogFlushMode flushMode = LogFlushMode.Buffered)
    {
        LoggingThreading.AssertMainThread("LoggingBootstrap.Reinitialize");

        lock (LifecycleLock)
        {
            LogPipelineShutdownResult shutdown = ShutdownCore(flushMode);
            if (!shutdown.IsComplete && shutdown.Status != LogPipelineShutdownStatus.NotStarted)
            {
                return new LoggingReinitializationResult(
                    shutdown,
                    new LoggingInitializationResult(LoggingInitializationStatus.ShutdownFailed, false));
            }

            if (LogRuntime.HasWriter)
            {
                return new LoggingReinitializationResult(
                    shutdown,
                    new LoggingInitializationResult(
                        LoggingInitializationStatus.ExistingProcessWriterNotOwned,
                        false));
            }

            LoggingInitializationResult initialization = InitializeCore(
                settings ?? LoggingProjectSettings.BuildSettings());
            return new LoggingReinitializationResult(shutdown, initialization);
        }
    }

    /// <summary>
    /// Removes the owned process writer, drains the owned pipeline, and releases its sinks.
    /// </summary>
    /// <remarks>
    /// Safe to call when nothing is initialized: it reports
    /// <see cref="LogPipelineShutdownStatus.NotStarted"/> and does nothing else. Safe to call from
    /// the window-close notification, which is why the drain happens before any node is freed.
    /// </remarks>
    public static LogPipelineShutdownResult Shutdown(LogFlushMode flushMode = LogFlushMode.Buffered)
    {
        lock (LifecycleLock)
        {
            return ShutdownCore(flushMode);
        }
    }

    /// <summary>The owned pipeline, or false when the backend is not running.</summary>
    public static bool TryGetOwnedPipeline(out LogPipeline pipeline)
    {
        pipeline = Volatile.Read(ref _ownedPipeline);
        return pipeline != null
            && (LifecycleState)Volatile.Read(ref _lifecycleState) == LifecycleState.Running;
    }

    /// <summary>
    /// True when a pipeline owned by this bootstrap is currently the process writer.
    /// </summary>
    public static bool IsInitialized
    {
        get
        {
            LogPipeline installed = Volatile.Read(ref _installedProcessWriter);
            return installed != null
                && (LifecycleState)Volatile.Read(ref _lifecycleState) == LifecycleState.Running
                && ReferenceEquals(LogRuntime.Writer, installed);
        }
    }

    /// <summary>
    /// Clears the owned-pipeline bookkeeping without touching the writer. Called by
    /// <see cref="LoggingRuntimeHost.ResetState"/> after a successful shutdown, so a fresh
    /// initialization starts from a clean slate.
    /// </summary>
    internal static void ResetForStateReset()
    {
        lock (LifecycleLock)
        {
            ResetProcessWriter();
            Volatile.Write(ref _ownedPipeline, null);
            Volatile.Write(ref _lifecycleState, (int)LifecycleState.Stopped);
        }
    }

    private static LoggingInitializationResult InitializeCore(LoggingSettings settings)
    {
        try
        {
            return InitializeCoreTransactional(settings);
        }
        catch
        {
            try
            {
                LogPipelineShutdownResult rollback = ShutdownCore(LogFlushMode.Buffered);
                if (!rollback.IsComplete && rollback.Status != LogPipelineShutdownStatus.NotStarted)
                {
                    EmergencyLogWriter.TryWrite(
                        "Logging initialization rollback did not complete. Ownership was retained for an "
                        + "explicit shutdown retry.");
                }
            }
            catch (Exception rollbackException) when (!(rollbackException is OutOfMemoryException))
            {
                EmergencyLogWriter.TryWrite(
                    "Logging initialization rollback failed. Ownership was retained for an explicit "
                    + "shutdown retry. " + rollbackException.GetType().Name);
            }

            throw;
        }
    }

    private static LoggingInitializationResult InitializeCoreTransactional(LoggingSettings settings)
    {
        LogPipelineOptions processingOptions = CreateProcessingOptions(settings);
        bool useGodotConsole = settings == null || settings.RegisterGodotConsoleSink;
        bool useConsole = settings != null && settings.RegisterConsoleLogSink;
        bool useFile = settings != null && settings.RegisterFileLogSink;

#if GODOT_WEB
        // The web export has no threads and no filesystem. Both substitutions are reported
        // rather than silent: a project that configured a file sink and shipped to the web needs
        // to know why its log file is empty.
        if (useFile)
        {
            EmergencyLogWriter.TryWrite(
                "The file sink is unavailable in Godot web exports and was not registered.");
            useFile = false;
        }
#endif

        if (!useGodotConsole && !useConsole && !useFile)
        {
            return new LoggingInitializationResult(
                LoggingInitializationStatus.NoSinksConfigured,
                false);
        }

        LogPipeline pipeline = CreatePipeline(settings, processingOptions);
        Volatile.Write(ref _ownedPipeline, pipeline);
        bool registeredAny = false;

        if (useGodotConsole)
        {
            GodotConsoleLogSinkOptions handoffOptions = CreateHandoffOptions(settings, processingOptions);
            registeredAny |= RegisterConfiguredSink(pipeline, new GodotConsoleLogSink(handoffOptions));
        }

        if (useConsole)
        {
            registeredAny |= RegisterConfiguredSink(pipeline, new ConsoleLogSink());
        }

        if (useFile && FileLogSink.IsSupported)
        {
            try
            {
                string filePath = LoggingPaths.ResolveLogFilePath(settings);
                FileLogSinkOptions fileOptions = CreateFileOptions(settings);
                var fileSink = new FileLogSink(filePath, fileOptions);
                registeredAny |= RegisterConfiguredSink(pipeline, fileSink);
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            {
                // A bad log path must not stop the game from starting. The console and handoff
                // sinks stay active, and the reason is written where it can still be seen.
                EmergencyLogWriter.TryWrite(
                    "File sink initialization failed; the remaining sinks stay active. "
                    + exception.GetType().Name);
            }
        }

        if (!registeredAny)
        {
            LogPipelineShutdownResult emptyShutdown = pipeline.Shutdown(LogFlushMode.Buffered);
            if (emptyShutdown.IsComplete)
            {
                Volatile.Write(ref _ownedPipeline, null);
                LoggingRuntimeHost.ResetAfterOwnedShutdown();
                return new LoggingInitializationResult(
                    LoggingInitializationStatus.NoSinksConfigured,
                    false);
            }

            Volatile.Write(ref _lifecycleState, (int)LifecycleState.ShutdownIncomplete);
            return new LoggingInitializationResult(
                LoggingInitializationStatus.ShutdownFailed,
                false);
        }

        if (settings != null)
        {
            pipeline.MinimumSeverity = settings.MinimumSeverity;
            pipeline.CategoryFilter = settings.CategoryFilter;
        }

        LoggingRuntimeHost.EnsureBootstrapInstance();

        if (LogRuntime.TryInstallWriter(pipeline)
            || ReferenceEquals(LogRuntime.Writer, pipeline))
        {
            Volatile.Write(ref _installedProcessWriter, pipeline);
            Volatile.Write(ref _lifecycleState, (int)LifecycleState.Running);
            return new LoggingInitializationResult(
                LoggingInitializationStatus.Initialized,
                true);
        }

        LogPipelineShutdownResult rollback = ShutdownCore(LogFlushMode.Buffered);
        if (!rollback.IsComplete && rollback.Status != LogPipelineShutdownStatus.NotStarted)
        {
            EmergencyLogWriter.TryWrite(
                "Logging initialization lost the process-writer race and rollback did not complete. "
                + "Ownership was retained for an explicit shutdown retry.");
            return new LoggingInitializationResult(
                LoggingInitializationStatus.ShutdownFailed,
                false);
        }

        return new LoggingInitializationResult(
            LoggingInitializationStatus.ExistingProcessWriterNotOwned,
            false);
    }

    private static bool RegisterConfiguredSink(LogPipeline pipeline, ILogSink sink)
    {
        LogSinkRegistrationResult result = pipeline.RegisterSink(sink, LogSinkRegistrationMode.UniqueExactType);
        if (result.IsRegistered)
        {
            return true;
        }

        if (result.CallerRetainsOwnership)
        {
            try
            {
                sink.Dispose();
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception exception)
            {
                EmergencyLogWriter.TryWrite(
                    "A rejected configured log sink could not be disposed. " + exception.GetType().Name);
            }
        }

        return false;
    }

    private static LogPipelineShutdownResult ShutdownCore(LogFlushMode flushMode)
    {
        LogPipeline owned = Volatile.Read(ref _ownedPipeline);
        LogPipeline installed = Volatile.Read(ref _installedProcessWriter);
        ResetProcessWriter();

        LogPipelineShutdownResult result;
        if (owned == null)
        {
            result = new LogPipelineShutdownResult(LogPipelineShutdownStatus.NotStarted, 0, true);
        }
        else
        {
            result = owned.Shutdown(flushMode);
        }

        if (result.IsComplete || result.Status == LogPipelineShutdownStatus.NotStarted)
        {
            if (owned != null && result.IsComplete)
            {
                LoggingRuntimeHost.ResetAfterOwnedShutdown();
            }

            Volatile.Write(ref _ownedPipeline, null);
            Volatile.Write(ref _lifecycleState, (int)LifecycleState.Stopped);
            return result;
        }

        // The pipeline is still running. Put the writer back if this bootstrap had installed it,
        // so a producer that logs during the retry window still reaches the sink being drained.
        if (installed != null
            && (LogRuntime.TryInstallWriter(installed) || ReferenceEquals(LogRuntime.Writer, installed)))
        {
            Volatile.Write(ref _installedProcessWriter, installed);
        }

        Volatile.Write(ref _lifecycleState, (int)LifecycleState.ShutdownIncomplete);
        return result;
    }

    private static void ResetProcessWriter()
    {
        LogPipeline installed = Interlocked.Exchange(ref _installedProcessWriter, null);
        if (installed != null)
        {
            LogRuntime.TryResetWriter(installed);
        }
    }

    private static LogPipelineOptions CreateProcessingOptions(LoggingSettings settings)
    {
        if (settings == null)
        {
            return ValidateQueueDepthAgainstPools(LogPipelineOptions.CreateValidated(null));
        }

        LogQueueOverflowPolicy overflowPolicy = settings.OverflowPolicy;
#if GODOT_WEB
        if (overflowPolicy == LogQueueOverflowPolicy.Block)
        {
            overflowPolicy = LogQueueOverflowPolicy.DropNewest;
            EmergencyLogWriter.TryWrite(
                "The Block overflow policy is unavailable in Godot web exports and was replaced "
                + "with DropNewest.");
        }
#endif

        return ValidateQueueDepthAgainstPools(LogPipelineOptions.CreateValidated(new LogPipelineOptions
        {
            MaxQueuedMessages = settings.MaxQueuedMessages,
            MaxQueuedCharacters = settings.MaxQueuedCharacters,
            MaxMessageCharacters = settings.MaxMessageCharacters,
            MaxCategoryCharacters = settings.MaxCategoryCharacters,
            MaxSourcePathCharacters = settings.MaxSourcePathCharacters,
            MaxMemberNameCharacters = settings.MaxMemberNameCharacters,
            MaxFilterCategories = settings.MaxFilterCategories,
            MaxFilterCharacters = settings.MaxFilterCharacters,
            ReservedCriticalMessages = settings.ReservedCriticalMessages,
            ReservedCriticalCharacters = settings.ReservedCriticalCharacters,
            ShutdownDrainTimeoutMs = settings.ShutdownDrainTimeoutMs,
            EnqueueBlockTimeoutMs = settings.EnqueueBlockTimeoutMs,
            MaintenanceIntervalMs = settings.MaintenanceIntervalMs,
            SinkFailureThreshold = settings.SinkFailureThreshold,
            OverflowPolicy = overflowPolicy,
            CriticalSeverity = settings.CriticalSeverity
        }));
    }

    /// <summary>
    /// Reports a queue depth the record pool cannot cover.
    /// </summary>
    /// <remarks>
    /// The pool only holds records that are <i>not</i> queued, so a burst that fills a queue deeper
    /// than the pool's retention capacity leaves nothing pooled and every further acquisition is a
    /// heap allocation on the producer thread — a bounded queue silently converting into per-pulse
    /// garbage. That is a silent, load-dependent failure: it never shows up in a light test run and
    /// only appears under the pulse traffic the bound exists to absorb. Reporting it here turns an
    /// invisible tuning trap into one line an operator can act on.
    /// </remarks>
    private static LogPipelineOptions ValidateQueueDepthAgainstPools(LogPipelineOptions options)
    {
        if (options.MaxQueuedMessages > LogEventPool.RetentionCapacity)
        {
            EmergencyLogWriter.TryWrite(
                "MaxQueuedMessages (" + options.MaxQueuedMessages + ") exceeds the record pool's "
                + "retention capacity (" + LogEventPool.RetentionCapacity + "). A burst that fills the "
                + "queue cannot be returned to the pool, so each pulse will allocate the shortfall on "
                + "the producer thread. Raise LogEventPool.RetentionCapacity in both repositories, or "
                + "lower MaxQueuedMessages.");
        }

        return options;
    }

    private static GodotConsoleLogSinkOptions CreateHandoffOptions(
        LoggingSettings settings,
        LogPipelineOptions processingOptions)
    {
        var options = new GodotConsoleLogSinkOptions
        {
            // The retention estimate is derived from the pipeline's own message limits so a
            // record that the pipeline accepted can always be admitted by the handoff. Deriving
            // it independently is how a handoff ends up silently dropping records that the
            // pipeline considered perfectly valid.
            MaximumRetainedEntryCharacters = GodotConsoleLogSinkOptions.EstimateRetainedCharacters(
                processingOptions.MaxMessageCharacters,
                processingOptions.MaxCategoryCharacters,
                processingOptions.MaxSourcePathCharacters),
            ReservedCriticalMessages = processingOptions.ReservedCriticalMessages,
            ReservedCriticalCharacters = processingOptions.ReservedCriticalCharacters,
            CriticalSeverity = processingOptions.CriticalSeverity
        };

        if (settings != null)
        {
            options.MaxQueuedMessages = settings.HandoffMaxQueuedMessages;
            options.MaxQueuedCharacters = settings.HandoffMaxQueuedCharacters;
            options.OverflowPolicy = settings.HandoffOverflowPolicy;
            options.OutputMode = settings.OutputMode;
            options.PadSeverity = settings.PadSeverity;
            options.PumpItemsPerFrame = settings.PumpItemsPerFrame;
            options.PumpBudgetMs = settings.PumpBudgetMs;
        }

        return GodotConsoleLogSinkOptions.CreateValidated(options);
    }

    private static LogPipeline CreatePipeline(LoggingSettings settings, LogPipelineOptions options)
    {
        LoggingExecutionMode mode = settings == null
            ? LoggingExecutionMode.Automatic
            : settings.ExecutionMode;

#if GODOT_WEB
        return LogPipelineFactory.CreateSingleThreaded(options);
#else
        switch (mode)
        {
            case LoggingExecutionMode.SingleThreaded:
                return LogPipelineFactory.CreateSingleThreaded(options);
            case LoggingExecutionMode.Threaded:
            case LoggingExecutionMode.Automatic:
                return LogPipelineFactory.CreateThreaded(options);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown logging execution mode.");
        }
#endif
    }

    private static FileLogSinkOptions CreateFileOptions(LoggingSettings settings)
    {
        return FileLogSinkOptions.CreateValidated(new FileLogSinkOptions
        {
            MaintenanceMode = settings.FileMaintenanceMode,
            MaxFileBytes = settings.MaxFileBytes,
            MaxArchiveFiles = settings.MaxArchiveFiles,
            FlushBatchSize = settings.FileFlushBatchSize,
            FlushIntervalMs = settings.FileFlushIntervalMs,
            DurableFlushOnFatal = settings.DurableFlushOnFatal,
            SourcePathMode = settings.FileSourcePathMode
        });
    }
}

using System;
using System.Diagnostics;
using System.Threading;
using CycloneGames.Logging.Pipeline;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Immutable snapshot of the main-thread handoff queue and its pump behaviour.
/// </summary>
public readonly struct LoggingHandoffStatistics
{
    public readonly int QueuedCount;
    public readonly int QueuedCharacters;
    public readonly int ReservedCount;
    public readonly int ReservedCharacters;
    public readonly int InFlightCount;
    public readonly int InFlightCharacters;
    public readonly int PeakQueuedCount;
    public readonly int PeakQueuedCharacters;
    public readonly long DroppedMessageCount;
    public readonly long DroppedCriticalCount;
    public readonly long AbandonedOnResetCount;

    /// <summary>Frames on which the pump ran.</summary>
    public readonly long PumpFrameCount;

    /// <summary>
    /// Frames on which the pump hit its item or time budget with records still queued.
    /// </summary>
    /// <remarks>
    /// This is the sharpest single indicator of a log pulse: a burst that a settled frame can
    /// absorb leaves this at zero, while a burst that outruns the per-frame budget — the case
    /// that shows up as a stair-step in the frame-time curve — increments it. Reading it
    /// alongside <see cref="PeakQueuedCount"/> separates "burst arrived" from "burst was
    /// costly".
    /// </remarks>
    public readonly long PumpBudgetExhaustedCount;

    /// <summary>Duration of the last pump, in microseconds.</summary>
    public readonly long LastPumpMicroseconds;

    /// <summary>Worst pump duration observed since the last reset, in microseconds.</summary>
    public readonly long MaxPumpMicroseconds;

    /// <summary>
    /// Cumulative time spent pumping, in microseconds. Paired with
    /// <see cref="PumpFrameCount"/>, this makes an interval average derivable without the
    /// sampler having to keep a second accumulator in step with this one.
    /// </summary>
    public readonly long TotalPumpMicroseconds;

    internal LoggingHandoffStatistics(
        int queuedCount,
        int queuedCharacters,
        int reservedCount,
        int reservedCharacters,
        int inFlightCount,
        int inFlightCharacters,
        int peakQueuedCount,
        int peakQueuedCharacters,
        long droppedMessageCount,
        long droppedCriticalCount,
        long abandonedOnResetCount,
        long pumpFrameCount,
        long pumpBudgetExhaustedCount,
        long lastPumpMicroseconds,
        long maxPumpMicroseconds,
        long totalPumpMicroseconds)
    {
        QueuedCount = queuedCount;
        QueuedCharacters = queuedCharacters;
        ReservedCount = reservedCount;
        ReservedCharacters = reservedCharacters;
        InFlightCount = inFlightCount;
        InFlightCharacters = inFlightCharacters;
        PeakQueuedCount = peakQueuedCount;
        PeakQueuedCharacters = peakQueuedCharacters;
        DroppedMessageCount = droppedMessageCount;
        DroppedCriticalCount = droppedCriticalCount;
        AbandonedOnResetCount = abandonedOnResetCount;
        PumpFrameCount = pumpFrameCount;
        PumpBudgetExhaustedCount = pumpBudgetExhaustedCount;
        LastPumpMicroseconds = lastPumpMicroseconds;
        MaxPumpMicroseconds = maxPumpMicroseconds;
        TotalPumpMicroseconds = totalPumpMicroseconds;
    }
}

/// <summary>
/// The main-thread end of the logging stack: a bounded, generational handoff queue plus the
/// in-tree node that drains it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What problem this solves.</b> The pipeline is allowed to emit from a worker thread, but
/// Godot's output functions and its whole scene tree are main-thread only. Something has to
/// carry records across that boundary without ever being able to stall either side. This class
/// is that something, and it is deliberately paranoid about three things:
/// </para>
/// <list type="number">
/// <item>
/// <b>Bounded.</b> Both a message count and a character count are enforced, with reserve
/// capacity carved out for records at or above the critical severity. A burst can lose records,
/// but it can never consume unbounded memory, and a fatal can still get out while the queue is
/// otherwise saturated.
/// </item>
/// <item>
/// <b>Per-frame budgeted.</b> The pump drains at most <c>PumpItemsPerFrame</c> records or
/// <c>PumpBudgetMs</c> milliseconds, whichever comes first. This is what turns a log pulse from
/// a frame-time spike into a short tail: the burst is spread over the frames that follow it
/// instead of being paid for in the frame it arrives on. The residual backlog is visible in the
/// statistics rather than hidden in the frame curve.
/// </item>
/// <item>
/// <b>Generationally safe.</b> Every reservation carries the generation it was taken in. A
/// reservation that outlives a teardown — an assembly reload, a scene change during a burst, a
/// paused editor — is discarded rather than committed into a queue whose layout no longer
/// matches. This is the defect class that makes handoff queues corrupt under hot reload, and it
/// is handled structurally instead of by hoping the timing never happens.
/// </item>
/// </list>
/// <para>
/// <b>Ownership.</b> Three distinct owners can exist and they do not share state carelessly: the
/// bootstrap (owns the pipeline and the host), an explicit sink adapter (counts itself in and
/// out and keeps the host alive for as long as it is registered), and the node itself (owns the
/// drain loop). <see cref="EnsureBootstrapInstance"/> marks the host as bootstrap-owned so a
/// later adapter teardown cannot free it from under the pipeline.
/// </para>
/// </remarks>
[GlobalClass]
public partial class LoggingRuntimeHost : Node
{
    /// <summary>Node name used when the host is created in code rather than as an autoload.</summary>
    public const string DefaultNodeName = "CycloneGamesLoggingHost";

    /// <summary>Default per-frame item budget for the handoff pump.</summary>
    public const int DefaultPumpItems = 256;

    /// <summary>Default per-frame millisecond budget for the handoff pump.</summary>
    public const double DefaultPumpBudgetMs = 2.0;

    /// <summary>Default per-frame millisecond budget for draining the pipeline into its sinks.</summary>
    public const double DefaultPipelinePumpBudgetMs = 1.0;

    /// <summary>
    /// Millisecond budget for the drain performed as the host leaves the tree.
    /// </summary>
    /// <remarks>
    /// Short on purpose. This runs during shutdown, so it must not be able to hold the process open for
    /// long, and the records it is recovering are the tail of a burst that was already in flight.
    /// </remarks>
    private const int ExitFlushTimeoutMs = 100;

    internal enum SubsystemResetStatus : byte
    {
        Reset = 0,
        OwnedShutdownIncomplete = 1,
        ExternalAdaptersPreserved = 2
    }

    internal readonly struct Reservation
    {
        internal readonly int Characters;
        internal readonly int Generation;

        internal Reservation(int characters, int generation)
        {
            Characters = characters;
            Generation = generation;
        }
    }

    private enum InitializationBlockReason : byte
    {
        None = 0,
        ShutdownIncomplete = 1,
        ExternalAdapterOwner = 2
    }

    private struct LogEntry
    {
        internal LogSeverity Severity;
        internal string Message;
#if TOOLS
        internal string SourcePath;
        internal int LineNumber;
        internal int RetainedCharacters;
#endif
    }

#if TOOLS
    /// <summary>
    /// Optional renderer installed by the editor layer to make source locations clickable.
    /// Returning true means the writer handled the record.
    /// </summary>
    internal delegate bool EditorConsoleWriter(LogSeverity severity, string message, string sourcePath, int lineNumber);
#endif

    private static readonly object QueueLock = new object();

    private static LoggingRuntimeHost _instance;
    private static LogEntry[] _entries = Array.Empty<LogEntry>();
    private static int _head;
    private static int _count;
    private static int _reservedCount;
    private static int _reservedCharacters;
    private static int _queuedCharacters;
    private static int _inFlightCount;
    private static int _inFlightCharacters;
    private static int _peakCount;
    private static int _peakCharacters;
    private static int _generation;
    private static int _adapterCount;
    private static bool _bootstrapHostOwned;
    private static bool _hostCleanupPending;
    private static LogPipeline _pumpDisabledPipeline;
    private static int _maxQueuedCharacters;
    private static int _reservedCriticalMessages;
    private static int _reservedCriticalCharacters;
    private static LogQueueOverflowPolicy _overflowPolicy = LogQueueOverflowPolicy.DropNewest;
    private static LogSeverity _criticalSeverity = LogSeverity.Error;
    /// <summary>
    /// Output routing for records the pipeline emits. Defaults to
    /// <see cref="LogOutputMode.StreamOnly"/>; see that type for the reasoning.
    /// </summary>
    /// <remarks>
    /// The addon's own failure reports — a duplicate host, a blocked initialization, a failed
    /// initialization — deliberately pass <see cref="LogOutputMode.EngineDiagnostics"/> instead of
    /// reading this field. Those are conditions <i>in the addon</i>, which is precisely what Godot's
    /// error surface exists for; this field governs the game's records, which are domain events. Keeping
    /// the two apart at the call site is the same distinction the default is built on.
    /// </remarks>
    private static LogOutputMode _outputMode = LogOutputMode.StreamOnly;
    private static int _pumpItemsPerFrame = DefaultPumpItems;
    private static double _pumpBudgetMs = DefaultPumpBudgetMs;
    private static double _pipelinePumpBudgetMs = DefaultPipelinePumpBudgetMs;
    private static long _droppedCount;
    private static long _droppedCriticalCount;
    private static long _abandonedOnResetCount;
    private static long _pumpFrameCount;
    private static long _pumpBudgetExhaustedCount;
    private static long _lastPumpMicroseconds;
    private static long _maxPumpMicroseconds;

    /// <summary>
    /// Cumulative pump time, so a consumer can compute a mean over a window by differencing two
    /// snapshots. A running maximum alone is not enough: it is monotonic, so it cannot tell a
    /// consumer what the *worst pump in this interval* was — see
    /// <see cref="LoggingDiagnostics.Sample"/>.
    /// </summary>
    private static long _totalPumpMicroseconds;
    private static bool _quitStarted;
    private static bool _quitting;
    private static volatile bool _initializationBlocked;
    private static InitializationBlockReason _initializationBlockReason;
#if TOOLS
    private static EditorConsoleWriter _editorConsoleWriter;
#endif

    // ---- Lifecycle ----

    /// <summary>
    /// Configures the handoff queue. Must run before any adapter is registered and while the
    /// queue is empty, because changing capacity under live reservations would invalidate them.
    /// </summary>
    internal static void Configure(GodotConsoleLogSinkOptions options)
    {
        LoggingThreading.AssertMainThread("LoggingRuntimeHost.Configure");
        GodotConsoleLogSinkOptions validated = GodotConsoleLogSinkOptions.CreateValidated(options);

        lock (QueueLock)
        {
            TryReleaseExternalOwnershipBlockNoLock();
            if (_initializationBlocked)
            {
                throw new InvalidOperationException(
                    "The logging handoff cannot be configured because the previous owner did not stop safely.");
            }

            if (_quitting)
            {
                throw new InvalidOperationException(
                    "The logging handoff cannot be configured after shutdown has started.");
            }

            if (_count != 0 || _reservedCount != 0 || _inFlightCount != 0)
            {
                throw new InvalidOperationException(
                    "The logging handoff cannot be reconfigured while it contains records.");
            }

            if (_adapterCount != 0)
            {
                throw new InvalidOperationException(
                    "The logging handoff cannot be reconfigured while adapters are registered.");
            }

            ApplyConfigurationNoLock(validated);
        }
    }

    /// <summary>
    /// Creates and mounts the host node if it does not exist yet.
    /// </summary>
    /// <remarks>
    /// The zero-configuration path mounts this node as an <c>autoload</c> from the plugin, which
    /// is Godot's equivalent of <c>DontDestroyOnLoad</c>: it is a child of the tree root, so it
    /// survives every <c>ChangeSceneToFile</c> and is torn down exactly once, at process exit.
    /// This method exists for hosts that drive their own composition root and never register the
    /// autoload; it mounts the same node under the root with the same lifetime semantics.
    /// </remarks>
    internal static void EnsureInstance()
    {
        if (_instance != null)
        {
            return;
        }

        if (_initializationBlocked)
        {
            throw new InvalidOperationException(
                "Logging handoff initialization is blocked because the previous owner did not stop safely.");
        }

        LoggingThreading.AssertMainThread("LoggingRuntimeHost.EnsureInstance");

        if (!(Engine.GetMainLoop() is SceneTree tree))
        {
            throw new InvalidOperationException(
                "The logging host requires a running SceneTree. Initialize the logging backend from a "
                + "scene's _Ready, from an autoload, or from the plugin's autoload rather than from "
                + "a MainLoop that is not a SceneTree.");
        }

        LoggingRuntimeHost host = null;
        try
        {
            host = new LoggingRuntimeHost { Name = DefaultNodeName };
            // A logging host that stops draining while the tree is paused would grow its backlog
            // during exactly the pause that precedes a crash report. It is pure bookkeeping, so
            // it keeps running while paused.
            host.ProcessMode = ProcessModeEnum.Always;

            // Claim the host slot *before* mounting. AddChild runs _Ready synchronously, and
            // _Ready's contract is "if _instance is already me, the bootstrap owns this node and
            // there is nothing to initialize". Assigning after the mount would make _Ready see a
            // null slot, conclude it is an engine-created autoload, and re-enter
            // LoggingBootstrap.Initialize from inside the bootstrap — building a second
            // pipeline and leaking the first one's worker thread. The ordering here is load-bearing.
            _instance = host;
            tree.Root.AddChild(host);
        }
        catch
        {
            _instance = null;
            if (host != null && GodotObject.IsInstanceValid(host))
            {
                host.QueueFree();
            }

            throw;
        }
    }

    /// <summary>
    /// Marks the host as owned by the bootstrap before mounting it, so a later adapter teardown
    /// cannot free the node the pipeline still depends on.
    /// </summary>
    internal static void EnsureBootstrapInstance()
    {
        LoggingThreading.AssertMainThread("LoggingRuntimeHost.EnsureBootstrapInstance");

        lock (QueueLock)
        {
            TryReleaseExternalOwnershipBlockNoLock();
            if (_initializationBlocked)
            {
                throw new InvalidOperationException(
                    "Logging handoff initialization is blocked because the previous owner did not stop safely.");
            }

            _bootstrapHostOwned = true;
            _hostCleanupPending = false;
        }

        try
        {
            EnsureInstance();
        }
        catch
        {
            lock (QueueLock)
            {
                _bootstrapHostOwned = false;
            }

            throw;
        }
    }

    // ---- Producer side ----

    /// <summary>
    /// Reserves queue budget for one record. The pair
    /// (<see cref="TryReserve"/>, <see cref="Commit"/>) exists so the adapter can reserve before
    /// formatting and only pay for building the string once admission is known — the same
    /// "decide, then format" ordering the pipeline uses for deferred message builders.
    /// </summary>
    internal static bool TryReserve(LogSeverity severity, int estimatedCharacters, out Reservation reservation)
    {
        lock (QueueLock)
        {
            if (_quitting)
            {
                reservation = default;
                RecordDropNoLock(severity);
                return false;
            }

            if (estimatedCharacters > _maxQueuedCharacters)
            {
                reservation = default;
                RecordDropNoLock(severity);
                return false;
            }

            int reservedCharacters = Math.Max(estimatedCharacters, 0);
            while (!HasCapacityNoLock(severity, reservedCharacters))
            {
                if (!TryEvictNoLock(severity))
                {
                    reservation = default;
                    RecordDropNoLock(severity);
                    return false;
                }
            }

            _reservedCount++;
            _reservedCharacters += reservedCharacters;
            reservation = new Reservation(reservedCharacters, _generation);
            return true;
        }
    }

    internal static void CancelReservation(Reservation reservation)
    {
        lock (QueueLock)
        {
            if (reservation.Generation == _generation)
            {
                ReleaseReservationNoLock(reservation.Characters);
            }
        }
    }

    internal static bool Commit(
        LogSeverity severity,
        string message,
        Reservation reservation
#if TOOLS
        ,
        string sourcePath = null,
        int lineNumber = 0
#endif
    )
    {
        lock (QueueLock)
        {
            if (reservation.Generation != _generation)
            {
                RecordDropNoLock(severity);
                return false;
            }

            ReleaseReservationNoLock(reservation.Characters);
            if (_quitting)
            {
                RecordDropNoLock(severity);
                return false;
            }

            long actualCharacters = message?.Length ?? 0;
#if TOOLS
            actualCharacters += sourcePath?.Length ?? 0;
#endif
            if (actualCharacters > _maxQueuedCharacters)
            {
                RecordDropNoLock(severity);
                return false;
            }

            int characters = (int)actualCharacters;
            if (characters > reservation.Characters)
            {
                RecordDropNoLock(severity);
                return false;
            }

            while (!HasCapacityNoLock(severity, characters))
            {
                if (!TryEvictNoLock(severity))
                {
                    RecordDropNoLock(severity);
                    return false;
                }
            }

            int tail = (_head + _count) % _entries.Length;
            _entries[tail].Severity = severity;
            _entries[tail].Message = message;
#if TOOLS
            _entries[tail].SourcePath = sourcePath;
            _entries[tail].LineNumber = lineNumber;
            _entries[tail].RetainedCharacters = characters;
#endif
            _count++;
            _queuedCharacters += characters;

            int retainedCount = _count + _inFlightCount;
            if (retainedCount > _peakCount)
            {
                _peakCount = retainedCount;
            }

            int retainedCharacters = _queuedCharacters + _inFlightCharacters;
            if (retainedCharacters > _peakCharacters)
            {
                _peakCharacters = retainedCharacters;
            }

            return true;
        }
    }

    // ---- Adapter registration ----

    /// <summary>
    /// Registers an explicit sink adapter and returns the generation it belongs to. The returned
    /// token is required for <see cref="UnregisterAdapter"/> so a stale adapter from a previous
    /// generation cannot decrement the current one's count.
    /// </summary>
    internal static int RegisterAdapter()
    {
        LoggingThreading.AssertMainThread("LoggingRuntimeHost.RegisterAdapter");

        lock (QueueLock)
        {
            TryReleaseExternalOwnershipBlockNoLock();
            if (_initializationBlocked || _quitStarted || _quitting)
            {
                throw new InvalidOperationException(
                    "Logging adapter registration is unavailable while initialization is blocked or shutting down.");
            }

            if (_entries.Length == 0)
            {
                ApplyConfigurationNoLock(GodotConsoleLogSinkOptions.CreateValidated(null));
            }

            _adapterCount++;
            return _generation;
        }
    }

    internal static void UnregisterAdapter(int generation)
    {
        bool cleanupOnCurrentThread = false;

        lock (QueueLock)
        {
            if (generation == _generation && _adapterCount > 0)
            {
                _adapterCount--;
                if (_adapterCount == 0)
                {
                    TryReleaseExternalOwnershipBlockNoLock();
                    if (!_bootstrapHostOwned)
                    {
                        _hostCleanupPending = true;
                        cleanupOnCurrentThread = LoggingThreading.IsMainThread;
                    }
                }
            }
        }

        if (cleanupOnCurrentThread)
        {
            CleanupUnusedHost();
        }
    }

    // ---- Statistics and flushing ----

    internal static LoggingHandoffStatistics GetStatistics()
    {
        lock (QueueLock)
        {
            return new LoggingHandoffStatistics(
                _count,
                _queuedCharacters,
                _reservedCount,
                _reservedCharacters,
                _inFlightCount,
                _inFlightCharacters,
                _peakCount,
                _peakCharacters,
                _droppedCount,
                _droppedCriticalCount,
                _abandonedOnResetCount,
                _pumpFrameCount,
                _pumpBudgetExhaustedCount,
                _lastPumpMicroseconds,
                _maxPumpMicroseconds,
                _totalPumpMicroseconds);
        }
    }

    /// <summary>
    /// Projects the handoff counters into the invariant harness's own parameter type.
    /// </summary>
    /// <remarks>
    /// Both callers (the editor dock and the headless runner) need the same eight-field mapping,
    /// and the harness must not read this type itself — the harness is pure .NET so that the same
    /// source also compiles into a console CI harness. Mapping in one place here is what keeps
    /// those two constraints from turning into two copies of the same code.
    /// </remarks>
    internal static LoggingSelfCheck.HandoffCounters ReadHarnessCounters()
    {
        LoggingHandoffStatistics handoff = GetStatistics();
        return new LoggingSelfCheck.HandoffCounters(
            handoff.QueuedCount,
            handoff.InFlightCount,
            handoff.ReservedCount,
            handoff.PeakQueuedCount,
            handoff.PumpFrameCount,
            handoff.PumpBudgetExhaustedCount,
            handoff.DroppedMessageCount,
            handoff.DroppedCriticalCount);
    }

    /// <summary>
    /// Drains up to <paramref name="budgetMilliseconds"/> worth of records. Off the main thread
    /// this degrades to a read-only idle check, because it can neither drain nor safely report.
    /// </summary>
    internal static bool TryFlushQueue(double budgetMilliseconds)
    {
        if (!LoggingThreading.IsMainThread)
        {
            return IsQueueIdle();
        }

        DrainQueue(int.MaxValue, budgetMilliseconds, enforceItems: false);
        return IsQueueIdle();
    }

    // ---- Pumping ----

    internal static void PumpOnce()
    {
        if (!LoggingThreading.IsMainThread)
        {
            return;
        }

        if (LoggingBootstrap.TryGetOwnedPipeline(out LogPipeline pipeline))
        {
            PumpPipelineWithinBudget(pipeline);
        }

        DrainQueue(_pumpItemsPerFrame, _pumpBudgetMs, enforceItems: true);
        CleanupUnusedHost();
    }

    /// <summary>
    /// Drains the pipeline into its sinks within a frame budget.
    /// </summary>
    /// <remarks>
    /// Only meaningful for the single-threaded execution mode — the threaded mode has its own
    /// worker and reports <c>Pump</c> as a no-op. A pipeline that throws here is remembered and
    /// never pumped again, because a faulted pipeline's failure is terminal and re-pumping it
    /// every frame would turn one defect into a per-frame error storm.
    /// </remarks>
    internal static void PumpPipelineWithinBudget(LogPipeline pipeline)
    {
        if (pipeline == null
            || ReferenceEquals(Volatile.Read(ref _pumpDisabledPipeline), pipeline))
        {
            return;
        }

        try
        {
            int budgetMs = _pipelinePumpBudgetMs <= 0.0
                ? 0
                : (int)Math.Max(1.0, Math.Ceiling(_pipelinePumpBudgetMs));
            pipeline.PumpWithinBudget(DefaultPumpItems, budgetMs);
        }
        catch
        {
            Volatile.Write(ref _pumpDisabledPipeline, pipeline);
            throw;
        }
    }

    private static void DrainQueue(int maxItems, double budgetMilliseconds, bool enforceItems)
    {
        long start = Stopwatch.GetTimestamp();
        long budgetTicks = budgetMilliseconds <= 0.0
            ? (enforceItems ? 0L : long.MaxValue)
            : (long)(Stopwatch.Frequency * (budgetMilliseconds / 1000.0));
        if (budgetTicks <= 0L && budgetMilliseconds > 0.0)
        {
            budgetTicks = 1L;
        }

        int processed = 0;
        bool exhausted = false;

        while (TryDequeue(out LogEntry entry))
        {
            try
            {
#if TOOLS
                Emit(entry.Severity, entry.Message, entry.SourcePath, entry.LineNumber);
#else
                Emit(entry.Severity, entry.Message);
#endif
            }
            finally
            {
                CompleteProcessing(GetRetainedCharacters(entry));
            }

            processed++;

            // Item bound first, then the time bound. The item bound is the one that keeps the
            // per-frame worst case predictable even when each individual record is cheap enough
            // that the clock never advances measurably; the time bound is what keeps the frame
            // safe when a record is expensive (a file sink write, a full disk). Either bound is
            // only recorded as "exhausted" when records are actually still waiting, so a burst
            // that lands exactly on the limit is not mistaken for pressure.
            if (enforceItems && processed >= maxItems)
            {
                exhausted = HasPendingNoLock();
                break;
            }

            if (budgetTicks != long.MaxValue && Stopwatch.GetTimestamp() - start >= budgetTicks)
            {
                exhausted = HasPendingNoLock();
                break;
            }
        }

        if (processed > 0)
        {
            long microseconds = (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency;
            lock (QueueLock)
            {
                _pumpFrameCount++;
                _lastPumpMicroseconds = microseconds;
                _totalPumpMicroseconds += microseconds;
                if (microseconds > _maxPumpMicroseconds)
                {
                    _maxPumpMicroseconds = microseconds;
                }

                if (exhausted)
                {
                    _pumpBudgetExhaustedCount++;
                }
            }
        }
    }

    private static bool HasPendingNoLock()
    {
        lock (QueueLock)
        {
            return _count != 0;
        }
    }

#if TOOLS
    internal static void SetEditorConsoleWriter(EditorConsoleWriter writer)
    {
        Volatile.Write(ref _editorConsoleWriter, writer);
    }
#endif

    // ---- Shutdown ----

    internal static void Shutdown(bool drain)
    {
        lock (QueueLock)
        {
            _quitStarted = true;
            _quitting = true;
            _bootstrapHostOwned = false;
            _hostCleanupPending = false;
        }

        if (drain && LoggingThreading.IsMainThread)
        {
            DrainQueue(int.MaxValue, 50.0, enforceItems: false);
        }
        else
        {
            DropAllQueued();
        }

        LoggingRuntimeHost instance;
        lock (QueueLock)
        {
            instance = _instance;
            _instance = null;
        }

        if (instance == null)
        {
            return;
        }

        if (!LoggingThreading.IsMainThread)
        {
            // Freeing a node off the main thread is not legal. The node stays alive and is
            // reclaimed with the tree; the queue is already terminal and drained, so nothing it
            // could still do is observable.
            return;
        }

        FreeHost(instance);
    }

    /// <summary>
    /// Releases the host and resets the queue after the bootstrap-owned pipeline has stopped,
    /// leaving the queue immediately reusable for a re-initialization.
    /// </summary>
    internal static void ResetAfterOwnedShutdown()
    {
        LoggingThreading.AssertMainThread("LoggingRuntimeHost.ResetAfterOwnedShutdown");

        DrainQueue(int.MaxValue, 50.0, enforceItems: false);

        LoggingRuntimeHost instance;
        lock (QueueLock)
        {
            _bootstrapHostOwned = false;
            if (_adapterCount != 0)
            {
                // Explicit adapters are not owned by the bootstrap. Keep their host and queue
                // intact instead of invalidating their generation underneath them.
                return;
            }

            instance = _instance;
            _instance = null;
            ResetQueueStateNoLock();
        }

        FreeHost(instance);
    }

    /// <summary>
    /// Full lifecycle reset used by tooling and by the editor's script-reload path.
    /// </summary>
    internal static SubsystemResetStatus ResetState(bool reportDiagnostics)
    {
        LoggingThreading.AssertMainThread("LoggingRuntimeHost.ResetState");

        LogPipelineShutdownResult ownedResult = LoggingBootstrap.Shutdown(LogFlushMode.Buffered);
        if (!ownedResult.IsComplete && ownedResult.Status != LogPipelineShutdownStatus.NotStarted)
        {
            lock (QueueLock)
            {
                _initializationBlocked = true;
                _initializationBlockReason = InitializationBlockReason.ShutdownIncomplete;
            }

            if (reportDiagnostics)
            {
                EmergencyLogWriter.TryWrite(
                    "The bootstrap-owned logging pipeline did not stop during a state reset. New "
                    + "initialization is blocked to preserve ownership safety.");
            }

            return SubsystemResetStatus.OwnedShutdownIncomplete;
        }

        bool explicitAdaptersSurvived;
        lock (QueueLock)
        {
            explicitAdaptersSurvived = _adapterCount != 0;
            if (explicitAdaptersSurvived)
            {
                _initializationBlocked = true;
                _initializationBlockReason = InitializationBlockReason.ExternalAdapterOwner;
            }
        }

        if (explicitAdaptersSurvived)
        {
            if (reportDiagnostics)
            {
                EmergencyLogWriter.TryWrite(
                    "Explicit Godot console sink owners survived a state reset. Their host and queue "
                    + "were preserved; dispose those owners before re-initializing the backend.");
            }

            return SubsystemResetStatus.ExternalAdaptersPreserved;
        }

        LoggingBootstrap.ResetForStateReset();
        LogMemoryPools.ClearIdleEntries();
        DrainQueue(int.MaxValue, 50.0, enforceItems: false);

        int abandonedEntries;
        LoggingRuntimeHost previousInstance;
        lock (QueueLock)
        {
            abandonedEntries = _count + _inFlightCount;
            _abandonedOnResetCount += abandonedEntries;
            previousInstance = _instance;
            ResetQueueStateNoLock();
        }

        FreeHost(previousInstance);

        if (abandonedEntries > 0 && reportDiagnostics)
        {
            EmergencyLogWriter.TryWrite(
                "Handoff records could not be drained during a state reset and were explicitly "
                + "abandoned: " + abandonedEntries + ".");
        }

        return SubsystemResetStatus.Reset;
    }

    // ---- Node lifecycle ----

    /// <summary>
    /// Claims the host slot and starts the backend when the engine created this node, and does
    /// nothing when the bootstrap did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why no exported flag decides this.</b> The obvious design is an
    /// <c>InitializeOnReady</c> export that the autoload scene sets to true. That relies on the
    /// exact name Godot gives a C# exported member in a <c>.tscn</c>, and getting it wrong fails
    /// <i>silently</i> — the assignment is ignored, <c>_Ready</c> does nothing, and the addon
    /// looks like it works while logging goes nowhere. Instead, the decision is derived from state
    /// the code already owns: <see cref="EnsureInstance"/> claims the slot before mounting, so a
    /// mounted-by-the-bootstrap node finds the slot already filled, while an engine-instantiated
    /// autoload finds it empty. No serialization, no name mapping, no silent failure mode.
    /// </para>
    /// <para>
    /// <b>Three ways this node can come to exist, all handled.</b>
    /// <list type="number">
    /// <item>Registered as the autoload — the engine creates it, the slot is empty, the backend
    /// starts. This is the zero-configuration path.</item>
    /// <item>Created by <see cref="LoggingBootstrap"/> — the slot is pre-claimed, so this
    /// method is inert and cannot recurse into the bootstrap.</item>
    /// <item>Dropped into a scene by hand, with no autoload and no bootstrap — the slot is empty,
    /// so the backend starts. Adding a host node is therefore a complete setup, which is a
    /// useful escape hatch for a host that drives its own composition root.</item>
    /// </list>
    /// A second host while one is already live is disabled rather than left pumping the same queue
    /// from two places.
    /// </para>
    /// </remarks>
    public override void _Ready()
    {
        if (_instance != null)
        {
            if (ReferenceEquals(_instance, this))
            {
                return;
            }

            SetProcess(false);
            GodotConsoleOutput.Write(
                LogSeverity.Warning,
                "[CycloneGames.Logging] A duplicate LoggingRuntimeHost was detected and disabled. "
                + "Register the plugin autoload, let LoggingBootstrap create the host, or place "
                + "one host node — not several.",
                LogOutputMode.EngineDiagnostics);
            return;
        }

        _instance = this;
        InitializeBackend();
    }

    private static void InitializeBackend()
    {
        try
        {
            LoggingInitializationResult result = LoggingBootstrap.Initialize();
            if (result.Status == LoggingInitializationStatus.ShutdownFailed)
            {
                GodotConsoleOutput.Write(
                    LogSeverity.Error,
                    "[CycloneGames.Logging] Automatic initialization is blocked because the previously "
                    + "owned pipeline did not finish shutting down.",
                    LogOutputMode.EngineDiagnostics);
            }
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            // A configuration mistake must not stop the game from starting: the backend stays
            // uninstalled, which makes every log call a no-op rather than a crash.
            GodotConsoleOutput.Write(
                LogSeverity.Error,
                "[CycloneGames.Logging] Automatic initialization failed. " + exception.GetType().Name
                + ": " + exception.Message,
                LogOutputMode.EngineDiagnostics);
        }
    }

    public override void _Process(double delta)
    {
        PumpOnce();
    }

    /// <summary>
    /// Last chance to get already-produced records out before the node leaves the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>NOTIFICATION_WM_CLOSE_REQUEST</c> is not enough.</b> It covers a user closing the window,
    /// but it is not the only way a process ends. The editor's Stop button and a <c>--quit-after</c> run
    /// can tear the tree down without ever sending it, and then everything still in flight is simply
    /// gone. That is not hypothetical: it is why a four-record demo could print two or three lines, which
    /// looks exactly like a logging bug and cost a debugging round to rule one out.
    /// </para>
    /// <para>
    /// <b>Drain-only, deliberately.</b> This forces the pipeline's backlog into the handoff queue and
    /// prints it, but does not uninstall or dispose the backend. If the host ever leaves and re-enters the
    /// tree, logging keeps working — <c>_Ready</c> does not run a second time, so a teardown here would
    /// leave the backend permanently dead. When the tree really is ending, the distinction is moot.
    /// </para>
    /// <para>
    /// Bounded by <see cref="ExitFlushTimeoutMs"/>: this runs during shutdown, so it must not be able to
    /// hold the process open. Records that do not make it out within the budget are lost, which is the
    /// same guarantee the queue gives at any other moment — a bounded queue does not promise delivery.
    /// </para>
    /// </remarks>
    public override void _ExitTree()
    {
        FlushBeforeTreeExit();
    }

    private static void FlushBeforeTreeExit()
    {
        // The node lives under the tree root, so Godot frees it on the main thread. Guard anyway: a
        // wrong-thread flush would print from the wrong thread rather than fail loudly, and Godot's
        // output functions are main-thread only.
        if (!LoggingThreading.IsMainThread)
        {
            return;
        }

        if (LoggingBootstrap.TryGetOwnedPipeline(out LogPipeline pipeline))
        {
            try
            {
                // Move the pipeline's backlog across the handoff boundary first, so the drain below has
                // something to print. Without this step a record that is still in the pipeline queue is
                // unreachable from here.
                pipeline.TryFlush(LogFlushMode.Buffered, ExitFlushTimeoutMs);
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            {
                // Nothing below this point can report the failure; the handoff drain still runs, so
                // whatever already crossed the boundary is not lost as well.
            }
        }

        DrainQueue(int.MaxValue, ExitFlushTimeoutMs, enforceItems: false);
    }

    public override void _Notification(int what)
    {
        switch ((long)what)
        {
            // Each constant is cast explicitly so the case labels match the scrutinee even if a
            // given Godot release declares the notification constants as `int` rather than `long`.
            // Godot's managed name keeps the WM uppercase (NOTIFICATION_WM_CLOSE_REQUEST), which is
            // easy to get wrong as `Wm` — the compiler catches it, but only if the file is compiled.
            case (long)NotificationWMCloseRequest:
                // The window is closing and the process is about to end. Stop the backend here
                // rather than in _ExitTree: at this point the tree is still fully alive, so the
                // file sink can still flush and the pipeline can still drain within its timeout.
                LoggingBootstrap.Shutdown(LogFlushMode.Buffered);
                break;

            case (long)NotificationApplicationPaused:
                // Mobile pause is the last reliable callback before the OS may suspend or kill
                // the process, so the buffered file sink is flushed durably enough to survive it.
                FlushOnPause();
                break;

            case (long)NotificationPredelete:
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }

                break;
        }
    }

    private static void FlushOnPause()
    {
        if (LoggingBootstrap.TryGetOwnedPipeline(out LogPipeline pipeline))
        {
            // A short, bounded flush: the platform may not give us long, and losing a few
            // buffered records is preferable to being killed mid-flush.
            pipeline.TryFlush(LogFlushMode.Buffered, 50);
        }

        DrainQueue(int.MaxValue, 20.0, enforceItems: false);
    }

    // ---- Internals ----

    private static void Emit(
        LogSeverity severity,
        string message
#if TOOLS
        ,
        string sourcePath,
        int lineNumber
#endif
    )
    {
#if TOOLS
        EditorConsoleWriter editorWriter = Volatile.Read(ref _editorConsoleWriter);
        if (editorWriter != null)
        {
            try
            {
                if (editorWriter(severity, message, sourcePath, lineNumber))
                {
                    return;
                }
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            {
                // The plain Godot output path stays the non-lossy fallback when the editor layer
                // is unavailable or changes shape.
            }
        }
#endif

        GodotConsoleOutput.Write(severity, message, _outputMode);
    }

    private static bool TryDequeue(out LogEntry entry)
    {
        lock (QueueLock)
        {
            if (_count == 0)
            {
                entry = default;
                return false;
            }

            entry = _entries[_head];
            _entries[_head] = default;
            _head = (_head + 1) % _entries.Length;
            _count--;
            int characters = GetRetainedCharacters(entry);
            _queuedCharacters -= characters;
            _inFlightCount++;
            _inFlightCharacters += characters;
            return true;
        }
    }

    private static void CompleteProcessing(int characters)
    {
        lock (QueueLock)
        {
            if (_inFlightCount > 0)
            {
                _inFlightCount--;
            }

            _inFlightCharacters -= Math.Max(characters, 0);
            if (_inFlightCharacters < 0)
            {
                _inFlightCharacters = 0;
            }
        }
    }

    private static bool IsQueueIdle()
    {
        lock (QueueLock)
        {
            return _count == 0 && _reservedCount == 0 && _inFlightCount == 0;
        }
    }

    private static bool HasCapacityNoLock(LogSeverity severity, int characters)
    {
        bool critical = severity >= _criticalSeverity;
        int messageLimit = critical ? _entries.Length : _entries.Length - _reservedCriticalMessages;
        int characterLimit = critical
            ? _maxQueuedCharacters
            : _maxQueuedCharacters - _reservedCriticalCharacters;

        return _count + _inFlightCount + _reservedCount < messageLimit
            && (long)_queuedCharacters + _inFlightCharacters + _reservedCharacters + characters <= characterLimit;
    }

    private static bool TryEvictNoLock(LogSeverity incomingSeverity)
    {
        bool incomingCritical = incomingSeverity >= _criticalSeverity;
        int offset = FindOldestNormalOffsetNoLock();

        if (offset < 0)
        {
            if (!incomingCritical || _overflowPolicy != LogQueueOverflowPolicy.DropOldest || _count == 0)
            {
                return false;
            }

            offset = 0;
        }
        else if (!incomingCritical && _overflowPolicy != LogQueueOverflowPolicy.DropOldest)
        {
            return false;
        }

        LogEntry dropped = RemoveAtOffsetNoLock(offset);
        RecordDropNoLock(dropped.Severity);
        return true;
    }

    private static int FindOldestNormalOffsetNoLock()
    {
        for (int offset = 0; offset < _count; offset++)
        {
            int index = (_head + offset) % _entries.Length;
            if (_entries[index].Severity < _criticalSeverity)
            {
                return offset;
            }
        }

        return -1;
    }

    private static LogEntry RemoveAtOffsetNoLock(int offset)
    {
        int index = (_head + offset) % _entries.Length;
        LogEntry removed = _entries[index];

        if (offset == 0)
        {
            _entries[_head] = default;
            _head = (_head + 1) % _entries.Length;
            _count--;
            _queuedCharacters -= GetRetainedCharacters(removed);
            return removed;
        }

        for (int current = offset; current < _count - 1; current++)
        {
            int destination = (_head + current) % _entries.Length;
            int source = (_head + current + 1) % _entries.Length;
            _entries[destination] = _entries[source];
        }

        int tail = (_head + _count - 1) % _entries.Length;
        _entries[tail] = default;
        _count--;
        _queuedCharacters -= GetRetainedCharacters(removed);
        return removed;
    }

    private static int GetRetainedCharacters(LogEntry entry)
    {
#if TOOLS
        return entry.RetainedCharacters;
#else
        return entry.Message?.Length ?? 0;
#endif
    }

    private static void ReleaseReservationNoLock(int reservedCharacters)
    {
        if (_reservedCount <= 0)
        {
            return;
        }

        _reservedCount--;
        _reservedCharacters -= Math.Min(Math.Max(reservedCharacters, 0), _maxQueuedCharacters);
        if (_reservedCharacters < 0)
        {
            _reservedCharacters = 0;
        }
    }

    private static void RecordDropNoLock(LogSeverity severity)
    {
        _droppedCount++;
        if (severity >= _criticalSeverity)
        {
            _droppedCriticalCount++;
        }
    }

    private static void DropAllQueued()
    {
        lock (QueueLock)
        {
            while (_count > 0)
            {
                LogEntry dropped = RemoveAtOffsetNoLock(0);
                RecordDropNoLock(dropped.Severity);
            }
        }
    }

    private static void ApplyConfigurationNoLock(GodotConsoleLogSinkOptions options)
    {
        _entries = new LogEntry[options.MaxQueuedMessages];
        _head = 0;
        _maxQueuedCharacters = options.MaxQueuedCharacters;
        _reservedCriticalMessages = options.ReservedCriticalMessages;
        _reservedCriticalCharacters = options.ReservedCriticalCharacters;
        _overflowPolicy = options.OverflowPolicy;
        _criticalSeverity = options.CriticalSeverity;
        _outputMode = options.OutputMode;
        _pumpItemsPerFrame = options.PumpItemsPerFrame;
        _pumpBudgetMs = options.PumpBudgetMs;
    }

    private static void ResetQueueStateNoLock()
    {
        _entries = Array.Empty<LogEntry>();
        _head = 0;
        _count = 0;
        _reservedCount = 0;
        _reservedCharacters = 0;
        _queuedCharacters = 0;
        _inFlightCount = 0;
        _inFlightCharacters = 0;
        _peakCount = 0;
        _peakCharacters = 0;
        _maxQueuedCharacters = 0;
        _reservedCriticalMessages = 0;
        _reservedCriticalCharacters = 0;
        _overflowPolicy = LogQueueOverflowPolicy.DropNewest;
        _criticalSeverity = LogSeverity.Error;
        _outputMode = LogOutputMode.StreamOnly;
        _droppedCount = 0;
        _droppedCriticalCount = 0;
        _abandonedOnResetCount = 0;
        _pumpFrameCount = 0;
        _pumpBudgetExhaustedCount = 0;
        _lastPumpMicroseconds = 0;
        _maxPumpMicroseconds = 0;
        _totalPumpMicroseconds = 0;
        _generation = unchecked(_generation + 1);
        _quitStarted = false;
        _quitting = false;
        _initializationBlocked = false;
        _initializationBlockReason = InitializationBlockReason.None;
        _bootstrapHostOwned = false;
        _hostCleanupPending = false;
        _pumpDisabledPipeline = null;
    }

    private static void FreeHost(LoggingRuntimeHost instance)
    {
        if (instance == null || !GodotObject.IsInstanceValid(instance))
        {
            return;
        }

        // Free(), not QueueFree(): the teardown path is what a state reset and the pre-delete
        // notification depend on, and a deferred free can lose the race against the very
        // assembly reload it exists to survive. The node is not the current scene and holds no
        // children, so freeing it here is safe.
        instance.Free();
    }

    private static void CleanupUnusedHost()
    {
        if (!LoggingThreading.IsMainThread)
        {
            return;
        }

        LoggingRuntimeHost instance;
        lock (QueueLock)
        {
            if (!_hostCleanupPending || _bootstrapHostOwned || _adapterCount != 0)
            {
                return;
            }

            if (_count != 0 || _reservedCount != 0 || _inFlightCount != 0)
            {
                return;
            }

            instance = _instance;
            _instance = null;
            _hostCleanupPending = false;
            ResetQueueStateNoLock();
            _instance = null;
        }

        FreeHost(instance);
    }

    private static void TryReleaseExternalOwnershipBlockNoLock()
    {
        if (_initializationBlockReason != InitializationBlockReason.ExternalAdapterOwner || _adapterCount != 0)
        {
            return;
        }

        _initializationBlocked = false;
        _initializationBlockReason = InitializationBlockReason.None;
    }
}

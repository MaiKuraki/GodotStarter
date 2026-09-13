using System;
using CycloneGames.Logging;
using CycloneGames.Logging.Pipeline;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Capacity and backpressure policy for the Godot main-thread handoff.
/// </summary>
/// <remarks>
/// <para>
/// <b>This queue is not the pipeline queue.</b> A record crosses two independent bounded
/// stages: the <see cref="LogPipeline"/> queue (producer threads → the pipeline's worker or the
/// pump) and this handoff queue (pipeline sink → the main thread). They are separate on purpose.
/// The pipeline queue protects the producer's frame from an overloaded backend; this queue
/// protects the main thread's frame from an overloaded console. Collapsing them into one would
/// mean a slow console could apply backpressure to a producer thread, which is exactly the
/// coupling the handoff exists to prevent.
/// </para>
/// <para>
/// <b>Why the retention estimate matters.</b> A handoff entry's cost is not just the message:
/// the adapter renders a category tag and a source location into the same string, and the
/// resulting string is what the queue actually holds. Reserving on the raw message length alone
/// would let a queue configured for 2 MB of retention hold several times that, which on a
/// memory-constrained mobile target is the difference between a budget and a hope.
/// <see cref="EstimateRetainedCharacters"/> is the accounting that keeps the character budget
/// honest, and it is deliberately generous rather than exact.
/// </para>
/// </remarks>
public sealed class GodotConsoleLogSinkOptions
{
    public const int DefaultMaxQueuedMessages = 4096;
    public const int DefaultMaxQueuedCharacters = 2 * 1024 * 1024;
    public const int DefaultReservedCriticalMessages = 64;
    public const int DefaultReservedCriticalCharacters = 64 * 1024;
    public const int MaxSupportedQueuedMessages = 256 * 1024;
    public const int MaxSupportedQueuedCharacters = 256 * 1024 * 1024;
    public const int MaxSupportedRetainedEntryCharacters = 16 * 1024 * 1024;

    internal const int FormattingOverheadCharacters = 256;

    /// <summary>
    /// How many copies of the source path the formatted string retains. The Godot editor's
    /// Output panel makes the location clickable, which needs the path twice in the rendered
    /// line; a shipping build keeps one. Godot has no equivalent of Unity's
    /// <c>LogOption.NoStacktrace</c> interaction here, so the difference is purely about the
    /// hyperlink.
    /// </summary>
#if TOOLS
    private const int RetainedSourcePathCopies = 3;
#else
    private const int RetainedSourcePathCopies = 2;
#endif

    public int MaxQueuedMessages = DefaultMaxQueuedMessages;

    public int MaxQueuedCharacters = DefaultMaxQueuedCharacters;

    public int MaximumRetainedEntryCharacters = EstimateRetainedCharacters(
        LogPipelineOptions.DefaultMaxMessageCharacters,
        LogPipelineOptions.DefaultMaxCategoryCharacters,
        LogPipelineOptions.DefaultMaxSourcePathCharacters);

    public int ReservedCriticalMessages = DefaultReservedCriticalMessages;

    public int ReservedCriticalCharacters = DefaultReservedCriticalCharacters;

    /// <summary>
    /// Handoff overflow policy. <see cref="LogQueueOverflowPolicy.Block"/> is rejected: blocking
    /// here would block the main thread, which is the one thing this queue must never do.
    /// </summary>
    public LogQueueOverflowPolicy OverflowPolicy = LogQueueOverflowPolicy.DropNewest;

    public LogSeverity CriticalSeverity = LogSeverity.Error;

    /// <summary>
    /// Output routing for records that survive the handoff. Defaults to
    /// <see cref="LogOutputMode.StreamOnly"/>: a record is a domain event, not an engine error,
    /// and routing them onto Godot's error surface would pollute the Debugger's Errors tab and CI
    /// error counts with ordinary game events. See <see cref="LogOutputMode"/> for the full
    /// argument, including why <c>Fatal</c> registers in both modes.
    /// </summary>
    public LogOutputMode OutputMode = LogOutputMode.StreamOnly;

    /// <summary>Per-frame item budget for the handoff pump.</summary>
    public int PumpItemsPerFrame = LoggingRuntimeHost.DefaultPumpItems;

    /// <summary>Per-frame millisecond budget for the handoff pump. Zero means "items only".</summary>
    public double PumpBudgetMs = LoggingRuntimeHost.DefaultPumpBudgetMs;

    public static GodotConsoleLogSinkOptions Default => new GodotConsoleLogSinkOptions();

    public GodotConsoleLogSinkOptions()
    {
    }

    public GodotConsoleLogSinkOptions(GodotConsoleLogSinkOptions source)
    {
        if (source == null)
        {
            source = Default;
        }

        MaxQueuedMessages = source.MaxQueuedMessages;
        MaxQueuedCharacters = source.MaxQueuedCharacters;
        MaximumRetainedEntryCharacters = source.MaximumRetainedEntryCharacters;
        ReservedCriticalMessages = source.ReservedCriticalMessages;
        ReservedCriticalCharacters = source.ReservedCriticalCharacters;
        OverflowPolicy = source.OverflowPolicy;
        CriticalSeverity = source.CriticalSeverity;
        OutputMode = source.OutputMode;
        PumpItemsPerFrame = source.PumpItemsPerFrame;
        PumpBudgetMs = source.PumpBudgetMs;
    }

    public GodotConsoleLogSinkOptions Clone()
    {
        return new GodotConsoleLogSinkOptions(this);
    }

    internal static GodotConsoleLogSinkOptions CreateValidated(GodotConsoleLogSinkOptions source)
    {
        var options = new GodotConsoleLogSinkOptions(source);
        options.NormalizeReservedCapacity();
        options.Validate();
        return options;
    }

    /// <summary>
    /// Upper bound on the characters one handoff entry can retain once formatted.
    /// </summary>
    internal static int EstimateRetainedCharacters(
        int messageCharacters,
        int categoryCharacters,
        int sourcePathCharacters)
    {
        long estimate = Math.Max(messageCharacters, 0);
        estimate += Math.Max(categoryCharacters, 0);
        estimate += (long)Math.Max(sourcePathCharacters, 0) * RetainedSourcePathCopies;
        estimate += FormattingOverheadCharacters;
        return estimate >= int.MaxValue ? int.MaxValue : (int)estimate;
    }

    private void NormalizeReservedCapacity()
    {
        if (MaxQueuedMessages > 0)
        {
            ReservedCriticalMessages = Math.Min(ReservedCriticalMessages, MaxQueuedMessages - 1);
        }

        if (MaxQueuedCharacters > 0 && MaximumRetainedEntryCharacters > 0)
        {
            int maximumReserve = Math.Max(0, MaxQueuedCharacters - MaximumRetainedEntryCharacters);
            ReservedCriticalCharacters = Math.Min(ReservedCriticalCharacters, maximumReserve);
        }

        if (PumpItemsPerFrame < 1)
        {
            PumpItemsPerFrame = LoggingRuntimeHost.DefaultPumpItems;
        }
    }

    private void Validate()
    {
        if (MaxQueuedMessages < 1 || MaxQueuedMessages > MaxSupportedQueuedMessages)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedMessages), "MaxQueuedMessages is outside the supported bounded range.");
        }

        if (MaxQueuedCharacters < 1 || MaxQueuedCharacters > MaxSupportedQueuedCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedCharacters), "MaxQueuedCharacters is outside the supported bounded range.");
        }

        if (MaximumRetainedEntryCharacters < 1
            || MaximumRetainedEntryCharacters > MaxSupportedRetainedEntryCharacters
            || MaximumRetainedEntryCharacters > MaxQueuedCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRetainedEntryCharacters),
                "MaximumRetainedEntryCharacters must be positive and cannot exceed MaxQueuedCharacters.");
        }

        if (ReservedCriticalMessages < 0 || ReservedCriticalMessages >= MaxQueuedMessages)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReservedCriticalMessages),
                "ReservedCriticalMessages must be non-negative and smaller than MaxQueuedMessages.");
        }

        if (ReservedCriticalCharacters < 0 || ReservedCriticalCharacters >= MaxQueuedCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReservedCriticalCharacters),
                "ReservedCriticalCharacters must be non-negative and smaller than MaxQueuedCharacters.");
        }

        if (OverflowPolicy != LogQueueOverflowPolicy.DropNewest
            && OverflowPolicy != LogQueueOverflowPolicy.DropOldest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OverflowPolicy),
                "The main-thread handoff supports only DropNewest or DropOldest; Block would stall the frame.");
        }

        if (!Enum.IsDefined(typeof(LogSeverity), CriticalSeverity) || CriticalSeverity == LogSeverity.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CriticalSeverity),
                "CriticalSeverity must be a logging severity.");
        }

        if (!Enum.IsDefined(typeof(LogOutputMode), OutputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(OutputMode), "Unknown output mode.");
        }

        if (PumpBudgetMs < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(PumpBudgetMs), "PumpBudgetMs cannot be negative.");
        }
    }
}

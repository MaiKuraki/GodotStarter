using CycloneGames.Logging.Pipeline;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Outcome of <see cref="LoggingBootstrap.Initialize"/>.
/// </summary>
public enum LoggingInitializationStatus : byte
{
    /// <summary>Initialization did not run because no sink was configured.</summary>
    NoSinksConfigured = 0,

    /// <summary>The owned pipeline is installed as the process writer.</summary>
    Initialized = 1,

    /// <summary>A pipeline owned by this bootstrap was already installed; nothing changed.</summary>
    AlreadyInitialized = 2,

    /// <summary>
    /// A writer was installed by someone else. The bootstrap will not adopt or replace it, so
    /// the caller's own composition root keeps ownership.
    /// </summary>
    ExistingProcessWriterNotOwned = 3,

    /// <summary>
    /// The previously owned pipeline did not stop within its drain timeout. Ownership is
    /// retained so the shutdown can be retried; re-initialization is refused until it completes.
    /// </summary>
    ShutdownFailed = 4
}

/// <summary>Result of <see cref="LoggingBootstrap.Initialize"/>.</summary>
public readonly struct LoggingInitializationResult
{
    public LoggingInitializationResult(LoggingInitializationStatus status, bool writerInstalled)
    {
        Status = status;
        WriterInstalled = writerInstalled;
    }

    public LoggingInitializationStatus Status { get; }

    /// <summary>
    /// True when the process writer observed at the end of the call is the pipeline this
    /// bootstrap owns or has adopted. False is not necessarily a failure — it is also the
    /// correct answer for <see cref="LoggingInitializationStatus.ExistingProcessWriterNotOwned"/>.
    /// </summary>
    public bool WriterInstalled { get; }

    public bool IsSuccess => Status == LoggingInitializationStatus.Initialized
        || Status == LoggingInitializationStatus.AlreadyInitialized;
}

/// <summary>
/// Pairing of the shutdown that preceded a re-initialization with its outcome.
/// </summary>
/// <remarks>
/// Both halves are reported even when the first one failed, because "the old backend would not
/// stop" and "the new backend would not start" have different remedies and a caller that only
/// received one of them would have to guess which happened.
/// </remarks>
public readonly struct LoggingReinitializationResult
{
    public LoggingReinitializationResult(
        LogPipelineShutdownResult shutdown,
        LoggingInitializationResult initialization)
    {
        Shutdown = shutdown;
        Initialization = initialization;
    }

    public LogPipelineShutdownResult Shutdown { get; }

    public LoggingInitializationResult Initialization { get; }
}

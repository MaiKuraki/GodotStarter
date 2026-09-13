using System;
using System.Diagnostics;
using Godot;

namespace CycloneGames.Logging.Godot;

/// <summary>
/// Declares and enforces the host layer's threading contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, and how it differs from the Unity original.</b> The Unity package
/// captures the main thread on the first call and compares
/// <c>Environment.CurrentManagedThreadId</c> afterwards. That works, but it means the first
/// caller silently becomes the authority: any code path that runs before the composition root
/// and touches the host from a worker would break the contract for the rest of the process.
/// Godot exposes the engine's own thread identities — <c>OS.get_main_thread_id()</c> and
/// <c>OS.get_thread_caller_id()</c> — so this layer can ask the engine instead of guessing.
/// There is consequently no capture step, no initialization order requirement, and no way for a
/// late or early caller to corrupt the answer.
/// </para>
/// <para>
/// <b>The contract.</b> Everything that touches Godot's scene tree, Godot's output functions, or
/// a <see cref="LoggingRuntimeHost"/>'s queue state must run on the main thread. The pipeline
/// kernel is the deliberate exception: <c>CycloneGames.Logging.Pipeline</c> is thread-safe on its
/// own terms, its worker thread owns the queue, and its record pool is built for concurrent
/// producers. That asymmetry is the whole point of the design — producers may log from any
/// thread with zero coordination, and the single main-thread-only step is the bounded handoff.
/// </para>
/// <para>
/// <b>Cost.</b> <see cref="IsMainThread"/> is two engine calls with no allocation and no lock;
/// <see cref="AssertMainThread"/> is compiled out entirely in release builds, so guarding a hot
/// loop costs nothing. Keep its argument side-effect free — in a release build the argument
/// expression is removed along with the call.
/// </para>
/// </remarks>
public static class LoggingThreading
{
    /// <summary>
    /// True when the caller is on Godot's main thread. Permissive before the engine reports its
    /// thread identities (both values zero), which only happens during very early bootstrap.
    /// </summary>
    public static bool IsMainThread
    {
        get
        {
            ulong mainThreadId = OS.GetMainThreadId();
            if (mainThreadId == 0UL)
            {
                return true;
            }

            return OS.GetThreadCallerId() == mainThreadId;
        }
    }

    /// <summary>
    /// Debug-only assertion that the caller is on the main thread. Throws at the violation site
    /// so a torn handoff surfaces where it happened, rather than later as a Godot error emitted
    /// from an arbitrary background thread.
    /// </summary>
    /// <param name="operation">Short description used in the failure message.</param>
    [Conditional("DEBUG")]
    public static void AssertMainThread(string operation)
    {
        ulong mainThreadId = OS.GetMainThreadId();
        if (mainThreadId == 0UL || OS.GetThreadCallerId() == mainThreadId)
        {
            return;
        }

        throw new InvalidOperationException(
            "CycloneGames.Logging: " + operation + " must run on the Godot main thread (main thread "
            + mainThreadId + ", caller thread " + OS.GetThreadCallerId()
            + "). See LoggingThreading for the host layer's concurrency contract.");
    }
}

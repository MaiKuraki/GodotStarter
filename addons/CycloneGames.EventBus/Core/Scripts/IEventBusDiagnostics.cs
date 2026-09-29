// Ported from CycloneGames.EventBus.Core (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.EventBus/README.md.
namespace CycloneGames.EventBus.Core
{
    /// <summary>
    /// Non-generic diagnostics view of a bus, so the facade, the debugger, and a future
    /// MemoryGovernance metric source can enumerate heterogeneous buses without knowing T.
    /// </summary>
    public interface IEventBusDiagnostics
    {
        string EventTypeName { get; }

        EventBusSnapshot GetSnapshot();
    }
}

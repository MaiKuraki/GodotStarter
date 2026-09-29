// Ported from CycloneGames.EventBus.Core (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.EventBus/README.md.
namespace CycloneGames.EventBus.Core
{
    /// <summary>
    /// Overflow behavior for a bounded command queue.
    /// </summary>
    public enum CommandOverflowPolicy
    {
        Drop = 0,
        FailFast = 1,
    }
}

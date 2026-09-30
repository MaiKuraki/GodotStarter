// Ported from CycloneGames.Factory.Runtime (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.Factory/README.md.
using System.Runtime.CompilerServices;

// Test assemblies assert on internal state (ownership-set contents, trim accounting, rollback
// counters) so those invariants stay testable without widening the public API.
[assembly: InternalsVisibleTo("CycloneGames.Factory.Tests.EditMode")]

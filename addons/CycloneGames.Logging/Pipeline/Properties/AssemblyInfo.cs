// Ported from CycloneGames.Logging.Pipeline (Unity). Keep both repositories in lockstep; see Modules/CycloneGames.Logging/README.md.
//
// In a Godot C# project every .cs file under the project folder compiles into a single
// assembly, so the `internal` members of the kernel are already visible to the
// CycloneGames.Logging.Godot host layer. No InternalsVisibleTo is required.
// The Core/Pipeline layering (no engine types) is enforced by Tools/Scripts/verify_CycloneGames.Logging.sh
// at the repository root instead of Unity assembly definitions. That script is deliberately outside
// the addon: Godot cannot run a shell script, so shipping one inside addons/ would be dead weight in
// every consuming project.
using System.Runtime.CompilerServices;

using System;
using System.Collections.Generic;
using System.Text;
using CycloneGames.Factory.Runtime;
using global::Godot;

namespace CycloneGames.Factory.Godot
{
    /// <summary>
    /// Dependency-free invariant harness for the Godot-facing layer of CycloneGames.Factory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file exists.</b> The ported NUnit suite in <c>Tests/</c> runs in a plain .NET host
    /// with no GodotSharp, so it cannot reach <c>Runtime/Godot/</c> at all — and that layer is new code
    /// with no Unity counterpart to inherit coverage from. The Unity original covered its adapters with
    /// EditMode tests that ran inside the editor; Godot's equivalent is a harness the engine itself can
    /// run. This is that harness, expressed the way the EventBus, Hash, Logging and IO harnesses are:
    /// runnable inside Godot with nothing installed, reporting PASS/FAIL/SKIP per named check.
    /// </para>
    /// <para>
    /// <b>Every assertion is a property, not a constant.</b> Pool counts are compared against the
    /// capacity policy that produced them; behaviour after release is checked by asking the engine
    /// whether the node is still usable, never by comparing against a golden count. A number a machine
    /// or a build could change is never asserted as a constant.
    /// </para>
    /// <para>
    /// <b>What cannot be checked here.</b> <c>QueueFree</c> defers deletion to the end of the frame, so
    /// a check that releases a node and then reads it back in the same call would observe the node
    /// still alive and prove nothing. The checks below therefore assert what is observable at once
    /// (the pool dropped ownership, the node is queued for deletion, the pool refuses to reuse it) and
    /// leave the actual reclamation to the engine, rather than inserting an artificial frame wait that
    /// would make the harness depend on the tree.
    /// </para>
    /// </remarks>
    public static class FactorySelfCheck
    {
        /// <summary>One named result. Mirrors the shape used by the other module harnesses.</summary>
        public readonly struct CheckResult
        {
            public readonly string Name;

            public readonly bool Passed;

            public readonly string Detail;

            internal CheckResult(string name, bool passed, string detail)
            {
                Name = name;
                Passed = passed;
                Detail = detail;
            }
        }

        /// <summary>Runs every check and formats the report. One line per check, then a summary line.</summary>
        public static string RunAndFormat()
        {
            List<CheckResult> results = RunAll();

            var builder = new StringBuilder();
            foreach (CheckResult result in results)
            {
                builder.Append(result.Passed ? "PASS  " : "FAIL  ").Append(result.Name);
                if (!string.IsNullOrEmpty(result.Detail))
                {
                    builder.Append(" — ").Append(result.Detail);
                }

                builder.Append('\n');
            }

            int passed = 0;
            foreach (CheckResult result in results)
            {
                if (result.Passed)
                {
                    passed++;
                }
            }

            builder.Append(passed).Append('/').Append(results.Count).Append(" checks passed");
            if (passed != results.Count)
            {
                builder.Append(" — ").Append(results.Count - passed).Append(" FAILED");
            }

            return builder.ToString();
        }

        public static List<CheckResult> RunAll()
        {
            return new List<CheckResult>
            {
                NodePoolReuseAvoidsInstantiation(),
                NodePoolRespectsHardCapacity(),
                SpawnedNodeIsProcessingAndVisible(),
                DespawnedNodeIsInert(),
                ReleaseDetachesOwnershipAndQueuesDeletion(),
                SpawnerRejectsWrongSceneRootType(),
                PackedSceneFactoryCreatesInactiveInstance(),
                TrimPolicyDestroysAboveSoftCapacity(),
            };
        }

        // ---- Godot adapter contracts -----------------------------------------------------------------

        private static CheckResult NodePoolReuseAvoidsInstantiation()
        {
            var scene = BuildScene();
            using var pool = new GodotNodeFastPool<Node3D>(scene, new PoolCapacitySettings(2, 8));

            Node3D first = pool.Spawn();
            pool.Despawn(first);
            Node3D second = pool.Spawn();

            bool ok = ReferenceEquals(first, second) && pool.CountInactive == 1;
            return new CheckResult(
                "godot.pool-reuse",
                ok,
                ok ? "a despawned node was handed back out instead of a new instance" : "the pool instantiated a second node");
        }

        private static CheckResult NodePoolRespectsHardCapacity()
        {
            var scene = BuildScene();
            using var pool = new GodotNodeFastPool<Node3D>(
                scene,
                new PoolCapacitySettings(0, 2, PoolOverflowPolicy.ReturnNull));

            int spawned = 0;
            for (int i = 0; i < 5; i++)
            {
                if (pool.TrySpawn(out Node3D node) && node != null)
                {
                    spawned++;
                }
            }

            bool ok = spawned == 2 && pool.CountAll == 2 && pool.Diagnostics.RejectedSpawns == 3;
            return new CheckResult(
                "godot.hard-capacity",
                ok,
                ok ? "three spawns past the ceiling were refused and counted" : $"spawned={spawned}, rejected={pool.Diagnostics.RejectedSpawns}");
        }

        private static CheckResult SpawnedNodeIsProcessingAndVisible()
        {
            var scene = BuildScene();
            using var pool = new GodotNodeFastPool<Node3D>(scene, new PoolCapacitySettings(0, 4));

            Node3D node = pool.Spawn();
            bool visible = node.Visible;
            bool processing = node.IsProcessing();

            pool.Despawn(node);

            bool ok = visible && processing;
            return new CheckResult(
                "godot.activation",
                ok,
                ok ? "a spawned node is visible and processing" : $"visible={visible}, processing={processing}");
        }

        /// <summary>
        /// Both the 2D and the 3D visibility branches are covered, because they are separate types.
        /// </summary>
        /// <remarks>
        /// Node3D is not a CanvasItem — Godot's 2D and 3D branches split at Node — so an adapter that
        /// only handled CanvasItem left every 3D node permanently visible while this check, written
        /// against a 2D node, stayed green. That is the failure this pair exists to catch: one type per
        /// branch, asserted separately, so neither can be silently skipped by the type test.
        /// </remarks>
        private static CheckResult DespawnedNodeIsInert()
        {
            var scene3D = BuildScene();
            using var pool3D = new GodotNodeFastPool<Node3D>(scene3D, new PoolCapacitySettings(0, 4));

            Node3D node3D = pool3D.Spawn();
            bool wasVisible3D = node3D.Visible;
            pool3D.Despawn(node3D);
            bool hidden3D = !node3D.Visible;
            bool idle3D = !node3D.IsProcessing();

            PackedScene scene2D = BuildControlScene();
            using var pool2D = new GodotNodeFastPool<Control>(scene2D, new PoolCapacitySettings(0, 4));

            Control node2D = pool2D.Spawn();
            bool wasVisible2D = node2D.Visible;
            pool2D.Despawn(node2D);
            bool hidden2D = !node2D.Visible;
            bool idle2D = !node2D.IsProcessing();

            bool ok = wasVisible3D && hidden3D && idle3D && wasVisible2D && hidden2D && idle2D;
            return new CheckResult(
                "godot.deactivation",
                ok,
                ok
                    ? "both the 2D and 3D branches hide and stop processing after despawn"
                    : $"3D visible={wasVisible3D}->{!hidden3D}, 2D visible={wasVisible2D}->{!hidden2D}, 3D idle={idle3D}, 2D idle={idle2D}");
        }

        private static CheckResult ReleaseDetachesOwnershipAndQueuesDeletion()
        {
            var scene = BuildScene();
            var pool = new GodotNodeFastPool<Node3D>(scene, new PoolCapacitySettings(0, 1));

            Node3D node = pool.Spawn();
            pool.Dispose();

            // Dispose routes every owned node through Release. The node is expected to be either already
            // freed or queued for deletion — what must NOT be true is that it is still live and reusable.
            bool detached = !GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion();

            bool ok = detached && pool.CountAll == 0 && pool.LifecycleState == PoolLifecycleState.Disposed;
            return new CheckResult(
                "godot.release-is-terminal",
                ok,
                ok ? "release detached ownership and queued the node for deletion" : $"valid={GodotObject.IsInstanceValid(node)}, countAll={pool.CountAll}");
        }

        private static CheckResult SpawnerRejectsWrongSceneRootType()
        {
            var scene = BuildScene();
            IGodotObjectSpawner spawner = new DefaultGodotObjectSpawner();

            // The scene root is a Node3D; asking for a Control must fail loudly rather than hand back
            // an object that would throw an InvalidCastException later inside a pool callback.
            bool threw = false;
            try
            {
                spawner.Create<Control>(scene);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            return new CheckResult(
                "godot.scene-root-type",
                threw,
                threw ? "a mismatched scene root was rejected" : "the spawner accepted a root of the wrong type");
        }

        private static CheckResult PackedSceneFactoryCreatesInactiveInstance()
        {
            var scene = BuildScene();
            var factory = new PackedSceneFactory<Node3D>(new DefaultGodotObjectSpawner(), scene);

            Node3D instance = factory.Create();
            bool inactive = !instance.IsProcessing();

            instance.Free();

            return new CheckResult(
                "godot.prefab-factory-inactive",
                inactive,
                inactive ? "the factory produced an inactive instance so the pool controls activation" : "the factory handed back an active node");
        }

        private static CheckResult TrimPolicyDestroysAboveSoftCapacity()
        {
            var scene = BuildScene();
            using var pool = new GodotNodeFastPool<Node3D>(
                scene,
                new PoolCapacitySettings(
                    softCapacity: 0,
                    hardCapacity: 4,
                    trimPolicy: PoolTrimPolicy.TrimOnDespawn));

            int returned = 0;
            var nodes = new Node3D[4];
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i] = pool.Spawn();
            }

            for (int i = 0; i < nodes.Length; i++)
            {
                pool.Despawn(nodes[i]);
            }

            returned = pool.CountInactive;

            bool ok = returned == 0 && pool.Diagnostics.TotalDestroyed == 4;
            return new CheckResult(
                "godot.trim-on-despawn",
                ok,
                ok ? "every returned node above the soft target was destroyed" : $"inactive={returned}, destroyed={pool.Diagnostics.TotalDestroyed}");
        }

        /// <summary>
        /// A one-node <see cref="Node3D"/> scene built in code. Instantiation is the behaviour under
        /// test, so the scene is constructed here rather than loaded from disk: a harness that needs a
        /// .tscn on disk cannot run in a bare project, and the other module harnesses all run with
        /// nothing installed.
        /// </summary>
        private static PackedScene BuildScene()
        {
            var root = new Node3D { Name = "SelfCheckSceneRoot" };
            var scene = new PackedScene();
            Error packError = scene.Pack(root);
            if (packError != Error.Ok)
            {
                root.Free();
                throw new InvalidOperationException($"PackedScene.Pack failed with {packError}.");
            }

            root.Free();
            return scene;
        }

        /// <summary>A <see cref="Control"/>-rooted scene, so the 2D visibility branch is exercised.</summary>
        private static PackedScene BuildControlScene()
        {
            var root = new Control { Name = "SelfCheckControlSceneRoot" };
            var scene = new PackedScene();
            Error packError = scene.Pack(root);
            if (packError != Error.Ok)
            {
                root.Free();
                throw new InvalidOperationException($"PackedScene.Pack failed with {packError}.");
            }

            root.Free();
            return scene;
        }
    }
}

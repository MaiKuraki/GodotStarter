@tool
extends EditorPlugin
## Editor entry point for the CycloneGames logging addon.
##
## Why this file is GDScript while every other file is C#: a C# class deriving from `EditorPlugin`
## is registered through an assembly-level attribute, and resolving that attribute while the game
## runs throws TypeLoadException (the `GodotSharpEditor` assembly only exists in editor mode). That
## exception aborts script registration for the whole assembly, so no C# script in the project
## could be instantiated. Keeping the entry point in GDScript removes the failure mode entirely.
## Every other editor file and the entire runtime remain C#.
##
## This shim owns exactly the two things an `EditorPlugin` can do and a plain C# node cannot:
##
##   1. It registers the runtime host as an autoload, which is Godot's equivalent of
##      `DontDestroyOnLoad` and the reason the addon needs no bootstrap call from user code.
##   2. It mounts the editor dock, which is where the live backend state and the invariant harness
##      live.
##
## Everything else — Project Settings registration, the dock's contents, the runtime backend — is
## C#. The shim deliberately holds no logging logic, so the C# layer can be reasoned about without
## reading GDScript.
##
## Paths are resolved relative to this script's own location rather than hardcoded as
## `res://addons/CycloneGames.Logging/...`. The addon is expected to be mountable under a different
## directory name via a junction, and a hardcoded `res://` prefix would break the moment that
## happened.

## Autoload name. Also the name the bootstrap-created host uses, so the two paths converge.
const AUTOLOAD_NAME := "CycloneGamesLoggingHost"
const AUTOLOAD_SETTING := "autoload/" + AUTOLOAD_NAME

## Project Settings key that lets a project opt out of the automatic autoload registration. Absent
## means "register", so a fresh clone is zero-configuration.
const REGISTER_AUTOLOAD_KEY := "cyclone_games_logging/registration/register_autoload"

const HOST_SCENE_RELATIVE := "/Runtime/Godot/godot_logging_host.tscn"
const DOCK_SCENE_RELATIVE := "/Editor/Scenes/logging_dock.tscn"
const ICON_RELATIVE := "/icons/logging_icon.svg"

## Fallback used only if the script's own resource path is unavailable (it is during the very
## first compile of a fresh clone).
const FALLBACK_DIR := "res://addons/CycloneGames.Logging"

## How many editor frames the dock install is retried before the failure is reported.
##
## A fresh clone is enabled before C# has ever been compiled, so `Editor/LoggingDock.cs` has no
## type yet and the dock scene cannot instantiate. That is expected exactly once and resolves after the
## first build, which is why this retries instead of reporting immediately: an error on a fresh clone is
## noise. But a dock that never appears is not cosmetic — the dock's `_Ready` is what registers the
## Project Settings section — so the retry is bounded and the failure is escalated rather than left as a
## warning nobody reads. ~10 s at 60 fps.
const DOCK_RETRY_FRAMES := 600

var _dock: Control
var _dock_retry_frames := 0

func _enter_tree() -> void:
	_register_autoload()
	if not _install_dock():
		_dock_retry_frames = 0
		set_process(true)

func _exit_tree() -> void:
	set_process(false)
	_remove_dock()

func _process(_delta: float) -> void:
	_dock_retry_frames += 1

	if _install_dock():
		set_process(false)
		return

	if _dock_retry_frames >= DOCK_RETRY_FRAMES:
		set_process(false)
		# Escalated, and it names the consequence. Silence here is what makes this failure expensive:
		# the autoload registers and records still reach the console, so the addon looks healthy while
		# the entire Project Settings surface is missing. That combination — partial function hiding a
		# missing piece — has already cost one debugging round.
		push_error("CycloneGames.Logging: the editor dock could not be installed after "
			+ str(DOCK_RETRY_FRAMES) + " frames, so Project Settings registration never ran. The "
			+ "runtime backend is unaffected and the autoload is registered, but the "
			+ "cyclone_games_logging/* section will be absent from Project Settings. Check that the "
			+ "project compiles (the dock's script is Editor/LoggingDock.cs) and re-enable the "
			+ "plugin.")

func _get_plugin_name() -> String:
	return "Logging"

func _get_plugin_icon() -> Texture2D:
	var icon_path := _plugin_dir() + ICON_RELATIVE
	if not ResourceLoader.exists(icon_path):
		return null
	return load(icon_path) as Texture2D

## Absolute directory of this plugin, derived from the script's own path so a relocated or
## junction-mounted addon keeps working.
func _plugin_dir() -> String:
	var script := get_script()
	if script != null and not script.resource_path.is_empty():
		return script.resource_path.get_base_dir()
	return FALLBACK_DIR

## Registers the runtime host as an autoload if the project has not already decided otherwise.
##
## Idempotent by design: an existing `autoload/...` entry is never overwritten, so a project that
## pointed the autoload at its own scene, or renamed it, keeps that decision.
##
## The autoload is deliberately NOT removed on `_exit_tree`. Removing it on every editor exit would
## rewrite `project.godot` twice per session — churn in version control for no functional gain —
## and disabling an editor dock should not silently disable a project's runtime logging backend.
## A project that wants the autoload gone sets `cyclone_games_logging/registration/register_autoload`
## to false in Project Settings and deletes the entry; that decision then sticks, because the shim
## reads it before touching anything.
func _register_autoload() -> void:
	if not ProjectSettings.get_setting(REGISTER_AUTOLOAD_KEY, true):
		return

	if ProjectSettings.has_setting(AUTOLOAD_SETTING):
		return

	var host_path := _plugin_dir() + HOST_SCENE_RELATIVE
	if not ResourceLoader.exists(host_path):
		# The C# side has not been imported or compiled yet. Saying so once is more useful than a
		# silent no-op that looks like the addon did nothing.
		push_warning("CycloneGames.Logging: the runtime host scene was not found at '" + host_path
			+ "'. The logging backend will not start automatically until the project has been "
			+ "compiled and the editor has imported the addon.")
		return

	add_autoload_singleton(AUTOLOAD_NAME, host_path)

## Mounts the editor dock, returning whether it is now installed.
##
## The dock's C# `_Ready` performs Project Settings registration, so installation and registration are
## the same event — which is exactly why a failure here is retried and then escalated rather than
## demoted to a warning. A false return means "not yet", and the caller decides how long "not yet" is
## allowed to last.
func _install_dock() -> bool:
	if is_instance_valid(_dock):
		return true

	var dock_path := _plugin_dir() + DOCK_SCENE_RELATIVE
	if not ResourceLoader.exists(dock_path):
		return false

	var packed := load(dock_path) as PackedScene
	if packed == null:
		return false

	var candidate := packed.instantiate() as Control
	if candidate == null:
		return false

	_dock = candidate
	add_control_to_dock(DOCK_SLOT_BOTTOM, _dock)
	return true

func _remove_dock() -> void:
	if not is_instance_valid(_dock):
		_dock = null
		return

	remove_control_from_docks(_dock)
	# free(), not queue_free(): a deferred free can lose the race against the very addon reload
	# this teardown exists for, and a surviving dock would keep signal handlers bound to a
	# managed object that no longer exists.
	_dock.free()
	_dock = null

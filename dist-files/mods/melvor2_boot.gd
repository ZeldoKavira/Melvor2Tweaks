extends Node
## Melvor2Tweaks startup check, registered as an autoload by override.cfg (beside the game executable).
##
## The mod needs one small patch in the game's GamesByMalcs.Melvor2.dll, and every game update from
## Steam replaces that DLL. On each start this compares the DLL with mods/patch.stamp (written by the
## patcher); when they differ - a game update, or a newer patch shipped with a mod update - it runs the
## patcher and restarts the game once so the patch takes effect. A failed patch never restarts, so this
## can't loop; the reason is written to mods/melvor2_boot.log.

const PATCHER_DIR := "mods/patcher"

func _ready() -> void:
	var root := _root()
	if root == "":
		return
	var dll := _game_dll(root)
	if dll == "":
		_log(root, "game DLL not found under " + root)
		return
	var stamp := _read_lines(root.path_join("mods/patch.stamp"))
	var expected_version := _read_lines(root.path_join(PATCHER_DIR + "/patch-version.txt"))
	var md5 := FileAccess.get_md5(dll)
	var up_to_date := stamp.size() >= 2 and stamp[0] == md5 \
		and (expected_version.is_empty() or stamp[1] == expected_version[0])
	if up_to_date:
		return

	var patcher := _patcher(root)
	if patcher == "" or not FileAccess.file_exists(patcher):
		_log(root, "patcher not found for this platform (" + OS.get_name() + "): " + patcher)
		return
	if OS.get_name() != "Windows":
		OS.execute("chmod", PackedStringArray(["+x", patcher]))  # zip extraction can drop the exec bit

	_log(root, "game DLL changed (or the patch was updated) - patching: " + patcher)
	var output := []
	var code := OS.execute(patcher, PackedStringArray([root]), output, true)
	_log(root, "patcher exit " + str(code) + ": " + "\n".join(PackedStringArray(output)).strip_edges())
	if code != 0:
		return

	# Patched: it takes effect on the next start, so restart once now.
	var after := _read_lines(root.path_join("mods/patch.stamp"))
	if after.size() >= 1 and after[0] == FileAccess.get_md5(dll):
		_log(root, "restarting to load the mod")
		OS.set_restart_on_exit(true, OS.get_cmdline_args())
		get_tree().quit()


## The folder holding mods/ and the game's data_* folder (the game folder; Contents/Resources on macOS).
func _root() -> String:
	var script_path: String = get_script().resource_path  # res://mods/melvor2_boot.gd
	var abs_path := ProjectSettings.globalize_path(script_path)
	if abs_path.begins_with("res://") or abs_path == "":
		abs_path = OS.get_executable_path().get_base_dir().path_join("mods/melvor2_boot.gd")
	return abs_path.get_base_dir().get_base_dir()


func _game_dll(root: String) -> String:
	var dir := DirAccess.open(root)
	if dir == null:
		return ""
	for d in dir.get_directories():
		if d.begins_with("data_GamesByMalcs.Melvor2_"):
			var candidate := root.path_join(d).path_join("GamesByMalcs.Melvor2.dll")
			if FileAccess.file_exists(candidate):
				return candidate
	return ""


func _patcher(root: String) -> String:
	var dir := root.path_join(PATCHER_DIR)
	match OS.get_name():
		"Windows":
			return dir.path_join("Melvor2Patcher.exe")
		"macOS":
			return dir.path_join("Melvor2Patcher-arm64" if Engine.get_architecture_name() == "arm64" else "Melvor2Patcher-x64")
		_:
			return dir.path_join("Melvor2Patcher")


func _read_lines(path: String) -> PackedStringArray:
	if not FileAccess.file_exists(path):
		return PackedStringArray()
	var text := FileAccess.get_file_as_string(path).strip_edges()
	return text.split("\n") if text != "" else PackedStringArray()


func _log(root: String, message: String) -> void:
	var path := root.path_join("mods/melvor2_boot.log")
	var f := FileAccess.open(path, FileAccess.READ_WRITE if FileAccess.file_exists(path) else FileAccess.WRITE)
	if f == null:
		return
	f.seek_end()
	f.store_line("[" + Time.get_datetime_string_from_system() + "] " + message)

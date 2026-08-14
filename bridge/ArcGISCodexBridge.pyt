import json
import os
import time
import traceback
from pathlib import Path

import arcpy


REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_QUEUE = REPO_ROOT / "bridge_queue"


class Toolbox:
    def __init__(self):
        self.label = "Codex ArcGIS Bridge"
        self.alias = "codexbridge"
        self.tools = [StartCodexBridge]


class StartCodexBridge:
    def __init__(self):
        self.label = "Start Codex Bridge"
        self.description = "Keep this tool running so Codex can control the current ArcGIS Pro project through ArcPy."
        self.canRunInBackground = False

    def getParameterInfo(self):
        queue = arcpy.Parameter(
            displayName="Bridge queue folder",
            name="queue_folder",
            datatype="DEFolder",
            parameterType="Required",
            direction="Input",
        )
        queue.value = str(DEFAULT_QUEUE)
        poll = arcpy.Parameter(
            displayName="Poll interval (seconds)",
            name="poll_seconds",
            datatype="GPDouble",
            parameterType="Optional",
            direction="Input",
        )
        poll.value = 0.5
        idle = arcpy.Parameter(
            displayName="Stop after idle minutes (0 = never)",
            name="idle_minutes",
            datatype="GPLong",
            parameterType="Optional",
            direction="Input",
        )
        idle.value = 480
        return [queue, poll, idle]

    def execute(self, parameters, messages):
        queue_root = Path(parameters[0].valueAsText)
        poll_seconds = max(float(parameters[1].value or 0.5), 0.1)
        idle_minutes = int(parameters[2].value or 0)
        jobs = queue_root / "jobs"
        processing = queue_root / "processing"
        results = queue_root / "results"
        for folder in (jobs, processing, results):
            folder.mkdir(parents=True, exist_ok=True)

        project = arcpy.mp.ArcGISProject("CURRENT")
        arcpy.AddMessage("Codex Bridge started for: {}".format(project.filePath or "unsaved project"))
        arcpy.AddMessage("Queue: {}".format(queue_root))
        arcpy.AddMessage("Cancel this geoprocessing tool to stop the bridge.")
        last_job = time.monotonic()

        while True:
            if bool(getattr(arcpy.env, "isCancelled", False)):
                arcpy.AddMessage("Codex Bridge cancelled.")
                break
            if idle_minutes > 0 and time.monotonic() - last_job > idle_minutes * 60:
                arcpy.AddMessage("Codex Bridge stopped after idle timeout.")
                break

            pending = sorted(jobs.glob("*.json"), key=lambda item: item.stat().st_mtime)
            if not pending:
                time.sleep(poll_seconds)
                continue

            for job_path in pending:
                claimed = processing / job_path.name
                try:
                    os.replace(str(job_path), str(claimed))
                except FileNotFoundError:
                    continue
                last_job = time.monotonic()
                job = None
                try:
                    job = json.loads(claimed.read_text(encoding="ascii"))
                    result = dispatch(project, job["action"], job.get("payload") or {})
                    response = {"ok": True, "result": json_safe(result)}
                except Exception as exc:
                    response = {"ok": False, "error": str(exc), "traceback": traceback.format_exc()}
                job_id = job.get("id") if isinstance(job, dict) else claimed.stem
                write_json_atomic(results / (job_id + ".json"), response)
                claimed.unlink(missing_ok=True)


def write_json_atomic(path, value):
    temp = path.with_suffix(".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False), encoding="utf-8")
    os.replace(str(temp), str(path))


def json_safe(value):
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    if isinstance(value, dict):
        return {str(key): json_safe(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_safe(item) for item in value]
    return str(value)


def choose_map(project, map_name=None):
    maps = project.listMaps(map_name) if map_name else project.listMaps()
    if map_name and not maps:
        raise ValueError("Map not found: {}".format(map_name))
    active = project.activeMap
    if not map_name and active is not None:
        return active
    if not maps:
        raise ValueError("The current project has no map")
    return maps[0]


def choose_layout(project, layout_name):
    layouts = project.listLayouts(layout_name)
    if not layouts:
        raise ValueError("Layout not found: {}".format(layout_name))
    return layouts[0]


def dispatch(project, action, payload):
    if action == "status":
        return {
            "project": project.filePath,
            "maps": [item.name for item in project.listMaps()],
            "layouts": [item.name for item in project.listLayouts()],
            "active_map": project.activeMap.name if project.activeMap else None,
        }

    if action == "add_data":
        map_obj = choose_map(project, payload.get("map_name"))
        added = []
        for path in payload["data_paths"]:
            layer = map_obj.addDataFromPath(path)
            added.append(layer.name if layer else path)
        return {"map": map_obj.name, "added": added}

    if action == "run_gp":
        toolbox_name, tool_name = payload["tool"].split(".", 1)
        tool = getattr(getattr(arcpy, toolbox_name), tool_name)
        arcpy.env.overwriteOutput = bool(payload.get("overwrite_output", False))
        gp_result = tool(*(payload.get("args") or []), **(payload.get("kwargs") or {}))
        outputs = []
        for index in range(getattr(gp_result, "outputCount", 0)):
            outputs.append(gp_result.getOutput(index))
        added = []
        if payload.get("add_outputs_to_map", True):
            map_obj = choose_map(project, payload.get("map_name"))
            for output in outputs:
                if output and arcpy.Exists(output):
                    try:
                        layer = map_obj.addDataFromPath(output)
                        added.append(layer.name if layer else output)
                    except Exception:
                        pass
        return {"tool": payload["tool"], "outputs": outputs, "added": added, "messages": gp_result.getMessages()}

    if action == "import_layout":
        before = {layout.name for layout in project.listLayouts()}
        project.importDocument(payload["pagx_path"])
        after = [layout.name for layout in project.listLayouts()]
        return {"layouts": after, "imported": [name for name in after if name not in before]}

    if action == "set_layout_text":
        layout = choose_layout(project, payload["layout_name"])
        elements = layout.listElements("TEXT_ELEMENT", payload["element_name"])
        if not elements:
            raise ValueError("Text element not found: {}".format(payload["element_name"]))
        elements[0].text = payload["text"]
        return {"layout": layout.name, "element": elements[0].name, "text": elements[0].text}

    if action == "zoom_layout":
        layout = choose_layout(project, payload["layout_name"])
        frames = layout.listElements("MAPFRAME_ELEMENT", payload.get("map_frame_name") or "*")
        if not frames:
            raise ValueError("No matching map frame found")
        frame = frames[0]
        layers = frame.map.listLayers(payload["layer_name"])
        if not layers:
            raise ValueError("Layer not found in map frame: {}".format(payload["layer_name"]))
        frame.camera.setExtent(frame.getLayerExtent(layers[0], False, True))
        frame.camera.scale *= float(payload.get("scale_factor", 1.1))
        return {"layout": layout.name, "map_frame": frame.name, "layer": layers[0].name, "scale": frame.camera.scale}

    if action == "export_layout":
        layout = choose_layout(project, payload["layout_name"])
        output = payload["output_file"]
        Path(output).parent.mkdir(parents=True, exist_ok=True)
        if output.lower().endswith(".pdf"):
            layout.exportToPDF(output, resolution=int(payload.get("dpi", 300)))
        elif output.lower().endswith(".png"):
            layout.exportToPNG(output, resolution=int(payload.get("dpi", 300)))
        else:
            raise ValueError("Only PDF and PNG are supported")
        return {"layout": layout.name, "output": output}

    if action == "save_copy":
        output = payload["output_aprx"]
        Path(output).parent.mkdir(parents=True, exist_ok=True)
        project.saveACopy(output)
        return {"output_aprx": output}

    raise ValueError("Unknown live action: {}".format(action))

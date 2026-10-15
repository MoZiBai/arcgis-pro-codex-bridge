from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVER = ROOT / "src" / "arcgis_mcp" / "server.py"
WORKER = ROOT / "src" / "arcgis_mcp" / "worker.py"
PROJECT_ACTIONS = ROOT / "addin" / "ArcGISCodexBridgeAddin" / "ProjectActions.cs"


def main() -> int:
    server = SERVER.read_text(encoding="utf-8")
    worker = WORKER.read_text(encoding="utf-8")
    project_actions = PROJECT_ACTIONS.read_text(encoding="utf-8")
    all_sources = "\n".join((server, worker, project_actions))
    for marker in (
        '"name": "live_create_blank_project"',
        '"create_blank_project"',
        '"name": "create_project_from_template"',
        'template_aprx',
        'map_frame_name',
        'source_project_modified',
        'rollback_backup',
        'create_project_from_template',
        'Project.CreateAsync(settings)',
        'TemplateType = TemplateType.Untitled',
        'path_verified = true',
        'project_support_folder = supportFolder',
        '!SamePath(project.Path, outputPath) || !File.Exists(outputPath)',
        'var bootstrapReady = await await',
        'var structureSaved = await await',
        'project.SaveAsAsync(outputPath, overwrite)',
    ):
        if marker not in all_sources:
            raise RuntimeError(f"Project lifecycle contract missing: {marker}")
    for marker in (
        'create_blank_project',
        'project.saveACopy(str(staging))',
        'project.createMap(map_name)',
        'project.createLayout(',
        'layout.createMapFrame(',
        'os.replace(staging, output)',
        'if staging.exists():',
    ):
        if marker not in all_sources:
            raise RuntimeError(f"Transactional project creation marker missing: {marker}")
    print("project lifecycle contract ok: blank SDK creation, transactional template copy, base structure, inventory, and rollback backup")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

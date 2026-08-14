from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ADDIN = ROOT / "addin" / "ArcGISCodexBridgeAddin"


def require(text: str, needle: str, source: str) -> None:
    if needle not in text:
        raise RuntimeError(f"Missing runtime-safety invariant in {source}: {needle}")


def reject(text: str, needle: str, source: str) -> None:
    if needle in text:
        raise RuntimeError(f"Unsafe runtime pattern remains in {source}: {needle}")


def main() -> int:
    framework = (ADDIN / "FrameworkActions.cs").read_text(encoding="utf-8")
    graphics = (ADDIN / "GraphicsActions.cs").read_text(encoding="utf-8")
    view = (ADDIN / "ViewInteractionActions.cs").read_text(encoding="utf-8")
    symbology = (ADDIN / "SymbologyActions.cs").read_text(encoding="utf-8")
    scene = (ADDIN / "SceneViewActions.cs").read_text(encoding="utf-8")

    require(framework, "Application.Current?.Dispatcher", "FrameworkActions.cs")
    require(framework, "OnUiAsync", "FrameworkActions.cs")
    require(framework, "can_close = Safe(() => pane.CanClose)", "FrameworkActions.cs")
    reject(graphics, "expanded = element.IsExpandedInTOC", "GraphicsActions.cs")
    require(view, "scale = Finite(camera.Scale)", "ViewInteractionActions.cs")
    require(view, "SetMapTOCContentAsync", "ViewInteractionActions.cs")
    require(view, "await OnUiAsync", "ViewInteractionActions.cs")
    require(view, "() => activeView.ShowPopup", "ViewInteractionActions.cs")
    require(view, "await OnUiAsync(view.ClosePopups", "ViewInteractionActions.cs")
    require(view, "return await QueuedTask.Run<object>(() =>", "ViewInteractionActions.cs")
    require(view, "view.MapToClient(mapPoint)", "ViewInteractionActions.cs")
    reject(view, "OnUiAsync(() => view.MapToClient", "ViewInteractionActions.cs")
    reject(view, "view.ShowPopup(member", "ViewInteractionActions.cs")
    reject(view, "return await QueuedTask.Run<object>(() =>\n        {\n            var view = ResolveActiveView(requestedMap);\n            view.ClosePopups();", "ViewInteractionActions.cs")
    map_actions = (ADDIN / "MapActions.cs").read_text(encoding="utf-8")
    require(map_actions, "z = Finite(camera.Z)", "MapActions.cs")
    reject(map_actions, "z = camera.Z,", "MapActions.cs")
    reject(symbology, "new CIMRenderingRule", "SymbologyActions.cs")
    require(symbology, "confirm_replace_definition=true is required", "SymbologyActions.cs")
    require(symbology, "Raster layer is not multidimensional", "SymbologyActions.cs")
    require(scene, 'status = "Error"', "SceneViewActions.cs")
    require(scene, "error = exception.Message", "SceneViewActions.cs")
    require(scene, "The active map has no animation", "SceneViewActions.cs")

    print(
        "runtime safety contract ok: TOC/popup UI dispatch, finite JSON values, guarded raster rules, "
        "multidimensional type checks, and resilient elevation errors"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

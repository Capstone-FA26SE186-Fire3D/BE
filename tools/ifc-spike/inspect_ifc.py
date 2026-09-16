"""Local IFC capability spike. Not an upload endpoint or production worker."""
import argparse
import hashlib
import json
import math
import platform
import sys
import time
from collections import Counter
from pathlib import Path

import ifcopenshell
import ifcopenshell.geom
import ifcopenshell.util.element
import ifcopenshell.util.unit

MAX_BYTES = 32 * 1024 * 1024  # Spike guard, not a product upload limit.


class InspectionError(ValueError):
    pass


def inspect(source: Path, max_bytes: int = MAX_BYTES) -> dict:
    started = time.perf_counter()
    if source.suffix.lower() != ".ifc":
        raise InspectionError("UNSUPPORTED_EXTENSION")
    if not source.is_file():
        raise InspectionError("FILE_NOT_FOUND")
    size = source.stat().st_size
    if size == 0:
        raise InspectionError("EMPTY_FILE")
    if size > max_bytes:
        raise InspectionError("FILE_TOO_LARGE")
    data = source.read_bytes()
    if len(data) > max_bytes:
        raise InspectionError("FILE_TOO_LARGE")
    stripped = data.strip()
    if not stripped.startswith(b"ISO-10303-21;") or not stripped.endswith(b"END-ISO-10303-21;"):
        raise InspectionError("INVALID_STEP_ENVELOPE")
    try:
        model = ifcopenshell.file.from_string(data.decode("utf-8"))
    except Exception as exc:
        raise InspectionError("IFC_PARSE_FAILED") from exc
    if model.schema not in ("IFC2X3", "IFC4"):
        raise InspectionError("UNSUPPORTED_SCHEMA")
    projects = model.by_type("IfcProject")
    if len(projects) != 1:
        raise InspectionError("EXPECTED_ONE_PROJECT")
    assignment = projects[0].UnitsInContext
    units = [] if assignment is None else assignment.Units
    lengths = [u for u in units if getattr(u, "UnitType", None) == "LENGTHUNIT"]
    if len(lengths) != 1:
        raise InspectionError("MISSING_OR_AMBIGUOUS_LENGTH_UNIT")
    scale = ifcopenshell.util.unit.calculate_unit_scale(model)
    if not math.isfinite(scale) or scale <= 0:
        raise InspectionError("INVALID_LENGTH_UNIT")
    storeys = model.by_type("IfcBuildingStorey")
    if not storeys:
        raise InspectionError("NO_STOREYS")
    settings = ifcopenshell.geom.settings()
    settings.set(settings.USE_WORLD_COORDS, True)
    meshes, issues = [], []
    for product in model.by_type("IfcElement"):
        if product.Representation is None:
            issues.append({"code": "NO_REPRESENTATION", "guid": product.GlobalId})
            continue
        try:
            shape = ifcopenshell.geom.create_shape(settings, product)
            vertices = shape.geometry.verts
            faces = shape.geometry.faces
            if not vertices or not faces or not all(math.isfinite(v) for v in vertices):
                raise ValueError("Empty or non-finite mesh")
            container = ifcopenshell.util.element.get_container(product)
            meshes.append({
                "guid": product.GlobalId, "type": product.is_a(),
                "storeyGuid": container.GlobalId if container and container.is_a("IfcBuildingStorey") else None,
                "vertices": len(vertices) // 3, "triangles": len(faces) // 3,
                "boundsMetres": {
                    "min": [min(vertices[axis::3]) for axis in range(3)],
                    "max": [max(vertices[axis::3]) for axis in range(3)],
                },
            })
        except Exception:
            issues.append({"code": "GEOMETRY_FAILED", "guid": product.GlobalId})
    if not meshes:
        issues.append({"code": "NO_MESHES"})
    return {
        "reportVersion": 1, "status": "passed" if meshes and not issues else "needs_review",
        "sourceSha256": hashlib.sha256(data).hexdigest(), "sourceBytes": len(data),
        "schema": model.schema, "lengthUnitToMetres": scale,
        "toolchain": {"ifcopenshell": ifcopenshell.version, "python": platform.python_version()},
        "counts": dict(sorted(Counter(e.is_a() for e in model.by_type("IfcElement")).items())),
        "storeys": [{"guid": s.GlobalId, "name": s.Name,
                     "elevationMetres": None if s.Elevation is None else s.Elevation * scale} for s in storeys],
        "meshes": meshes, "issues": issues,
        "elapsedSeconds": round(time.perf_counter() - started, 4),
        "limitations": ["Synthetic/local capability check, not fire-safety or IFC-schema certification",
                        "No GLB, navigation graph, durable job, storage, API or Unity integration"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.output and args.output.resolve() == args.source.resolve():
        parser.error("Output must not overwrite the IFC source")
    try:
        report = inspect(args.source)
        code = 0 if report["status"] == "passed" else 2
    except (InspectionError, OSError) as exc:
        report = {"reportVersion": 1, "status": "rejected", "error": str(exc)}
        code = 1
    payload = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(payload + "\n", encoding="utf-8")
    print(payload)
    return code


if __name__ == "__main__":
    sys.exit(main())
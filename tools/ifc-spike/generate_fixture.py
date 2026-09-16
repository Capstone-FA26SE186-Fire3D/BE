"""Generate our own tiny IFC4 fixture: two storeys, one 4x0.2x3m wall each."""
import argparse
import uuid
from pathlib import Path

import ifcopenshell
import ifcopenshell.api.aggregate
import ifcopenshell.api.context
import ifcopenshell.api.geometry
import ifcopenshell.api.root
import ifcopenshell.api.spatial
import ifcopenshell.api.unit
import numpy as np


def generate(destination: Path, millimetres: bool = True) -> None:
    model = ifcopenshell.file(schema="IFC4")
    project = ifcopenshell.api.root.create_entity(model, ifc_class="IfcProject", name="Fire3D synthetic fixture")
    length = ifcopenshell.api.unit.add_si_unit(model, unit_type="LENGTHUNIT", prefix="MILLI" if millimetres else None)
    ifcopenshell.api.unit.assign_unit(model, units=[length])
    context = ifcopenshell.api.context.add_context(model, context_type="Model")
    body = ifcopenshell.api.context.add_context(model, context_type="Model", context_identifier="Body", target_view="MODEL_VIEW", parent=context)
    site = ifcopenshell.api.root.create_entity(model, ifc_class="IfcSite", name="Synthetic site")
    building = ifcopenshell.api.root.create_entity(model, ifc_class="IfcBuilding", name="Synthetic building")
    ifcopenshell.api.aggregate.assign_object(model, products=[site], relating_object=project)
    ifcopenshell.api.aggregate.assign_object(model, products=[building], relating_object=site)
    for index in range(2):
        storey = ifcopenshell.api.root.create_entity(model, ifc_class="IfcBuildingStorey", name=f"Level {index}")
        storey.Elevation = float(index * 3000 if millimetres else index * 3)
        ifcopenshell.api.aggregate.assign_object(model, products=[storey], relating_object=building)
        matrix = np.eye(4)
        matrix[2, 3] = index * 3
        ifcopenshell.api.geometry.edit_object_placement(model, product=storey, matrix=matrix)
        wall = ifcopenshell.api.root.create_entity(model, ifc_class="IfcWall", name=f"Wall {index}")
        ifcopenshell.api.spatial.assign_container(model, products=[wall], relating_structure=storey)
        representation = ifcopenshell.api.geometry.add_wall_representation(model, context=body, length=4.0, height=3.0, thickness=0.2)
        ifcopenshell.api.geometry.assign_representation(model, product=wall, representation=representation)
        ifcopenshell.api.geometry.edit_object_placement(model, product=wall, matrix=matrix)
    for index, entity in enumerate(model.by_type("IfcRoot")):
        entity.GlobalId = ifcopenshell.guid.compress(uuid.uuid5(uuid.NAMESPACE_URL, f"fire3d:fixture:{index}").hex)
    model.header.file_name.name = "fire3d-synthetic.ifc"
    model.header.file_name.time_stamp = "2026-09-15T00:00:00"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(model.to_string(), encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--metres", action="store_true")
    args = parser.parse_args()
    generate(args.destination, millimetres=not args.metres)
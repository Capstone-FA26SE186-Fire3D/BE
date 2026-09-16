import hashlib
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import ifcopenshell
from generate_fixture import generate
from inspect_ifc import InspectionError, inspect


class IfcSpikeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.source = Path(self.temp.name) / "fixture.ifc"
        generate(self.source)

    def test_metadata_geometry_and_hash(self):
        report = inspect(self.source)
        self.assertEqual("passed", report["status"])
        self.assertEqual("IFC4", report["schema"])
        self.assertEqual({"IfcWall": 2}, report["counts"])
        self.assertEqual([0.0, 3.0], [s["elevationMetres"] for s in report["storeys"]])
        self.assertEqual(hashlib.sha256(self.source.read_bytes()).hexdigest(), report["sourceSha256"])
        self.assertEqual(2, len(report["meshes"]))
        for mesh in report["meshes"]:
            self.assertGreater(mesh["triangles"], 0)
            self.assertIsNotNone(mesh["storeyGuid"])
            self.assertAlmostEqual(4.0, mesh["boundsMetres"]["max"][0])
            self.assertAlmostEqual(0.2, mesh["boundsMetres"]["max"][1])
        self.assertAlmostEqual(6.0, max(m["boundsMetres"]["max"][2] for m in report["meshes"]))

    def test_metre_and_millimetre_inputs_produce_same_geometry(self):
        mm = inspect(self.source)
        generate(self.source, millimetres=False)
        metres = inspect(self.source)
        self.assertEqual(0.001, mm["lengthUnitToMetres"])
        self.assertEqual(1.0, metres["lengthUnitToMetres"])
        for first, second in zip(mm["meshes"], metres["meshes"]):
            for bound in ("min", "max"):
                for a, b in zip(first["boundsMetres"][bound], second["boundsMetres"][bound]):
                    self.assertAlmostEqual(a, b)

    def test_reject_empty_and_truncated(self):
        for content, code in [(b"", "EMPTY_FILE"), (b"ISO-10303-21;\nHEADER;", "INVALID_STEP_ENVELOPE")]:
            self.source.write_bytes(content)
            with self.assertRaisesRegex(InspectionError, code):
                inspect(self.source)

    def test_reject_wrong_extension_missing_and_oversize(self):
        with self.assertRaisesRegex(InspectionError, "UNSUPPORTED_EXTENSION"):
            inspect(self.source.with_suffix(".txt"))
        with self.assertRaisesRegex(InspectionError, "FILE_NOT_FOUND"):
            inspect(self.source.parent / "missing.ifc")
        with self.assertRaisesRegex(InspectionError, "FILE_TOO_LARGE"):
            inspect(self.source, max_bytes=1)

    def test_missing_units_rejected(self):
        model = ifcopenshell.open(str(self.source))
        model.by_type("IfcProject")[0].UnitsInContext = None
        model.write(str(self.source))
        with self.assertRaisesRegex(InspectionError, "MISSING_OR_AMBIGUOUS_LENGTH_UNIT"):
            inspect(self.source)

    def test_missing_representation_requires_review(self):
        model = ifcopenshell.open(str(self.source))
        model.by_type("IfcWall")[0].Representation = None
        model.write(str(self.source))
        report = inspect(self.source)
        self.assertEqual("needs_review", report["status"])
        self.assertEqual("NO_REPRESENTATION", report["issues"][0]["code"])

    def test_cli_exit_and_source_not_overwritten(self):
        script = str(Path(__file__).with_name("inspect_ifc.py"))
        before = self.source.read_bytes()
        rejected = subprocess.run([sys.executable, script, str(self.source), "--output", str(self.source)], capture_output=True)
        self.assertNotEqual(0, rejected.returncode)
        self.assertEqual(before, self.source.read_bytes())
        output = self.source.with_suffix(".json")
        passed = subprocess.run([sys.executable, script, str(self.source), "--output", str(output)], capture_output=True)
        self.assertEqual(0, passed.returncode, passed.stderr.decode())
        self.assertTrue(output.is_file())


if __name__ == "__main__":
    unittest.main()
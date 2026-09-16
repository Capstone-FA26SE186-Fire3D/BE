"""Fail CI when DB tests were silently skipped or never discovered."""
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

files = list(Path(sys.argv[1]).glob("*.trx"))
assert files, "No TRX test evidence"
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
for path in files:
    counters = ET.parse(path).find(".//t:Counters", ns)
    assert counters is not None, f"Missing counters: {path}"
    total = int(counters.attrib["total"])
    assert total > 0 and int(counters.attrib["passed"]) == total, counters.attrib
    print(f"PASS: {total} tests executed and passed")

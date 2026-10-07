"""Usage: python tools/generate-api-inventory.py captured-openapi.json docs/api-route-inventory.md"""
import json
import sys
from pathlib import Path
doc = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
rows = []
for path, item in sorted(doc["paths"].items()):
    for method, operation in sorted(item.items()):
        if method not in {"get", "post", "put", "patch", "delete", "head", "options"}:
            continue
        security = operation.get("security", doc.get("security", []))
        auth = ", ".join(sorted({key for requirement in security for key in requirement})) or "Anonymous"
        headers = ", ".join(p["name"] for p in operation.get("parameters", []) if p.get("in") == "header" and p.get("required"))
        responses = ", ".join(sorted(operation.get("responses", {})))
        rows.append(f"| {method.upper()} | {path} | {auth} | {headers or '—'} | {responses} |")
text = f"""# Route inventory generated from OpenAPI

{len(rows)} HTTP operations in the captured source test artifact. Includes metadata only; this is not a completion or deployment checklist. Run the generator again after route/metadata changes. Role/tenant/lifecycle and feature evidence: [selected-api-contract.md](selected-api-contract.md), [auth-api-checklist.md](auth-api-checklist.md), [api-implementation-checklist.md](api-implementation-checklist.md).

| Method | Endpoint | Authentication metadata | Required headers | Declared responses |
|---|---|---|---|---|
"""
Path(sys.argv[2]).write_text(text + "\n".join(rows) + "\n", encoding="utf-8")

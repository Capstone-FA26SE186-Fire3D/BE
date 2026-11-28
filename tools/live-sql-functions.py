# Lists, for every SQL function, the file and migration of its live definition (the latest migration that embeds a file defining it).
# Usage (from BE/): python tools/live-sql-functions.py Fire3D .codex/sql-live-functions.tsv
import re, glob, os, sys
os.chdir(sys.argv[1])
csproj = open('Fire3D.Infrastructure/Fire3D.Infrastructure.csproj', encoding='utf-8').read()
res = {m.group(2): m.group(1).replace(chr(92), '/') for m in re.finditer(r'Include="([^"]+)" LogicalName="([^"]+)"', csproj)}
migs = []
for f in glob.glob('Fire3D.Infrastructure/Migrations/*.cs'):
    if f.endswith('.Designer.cs') or 'Snapshot' in f:
        continue
    s = open(f, encoding='utf-8-sig').read()
    m = re.search(r'\[Migration\("(\d+)_(\w+)"\)\]', s)
    if not m:
        continue
    names = re.findall(r'GetManifestResourceStream\("([^"]+)"\)', s)
    names += re.findall(r'"(Fire3D\.Infrastructure\.[\w.]+\.sql)"', s)
    inline = f if re.search(r'CREATE (OR REPLACE )?FUNCTION', s) else None
    migs.append((m.group(1), m.group(2), sorted(set(names)), inline))
migs.sort()
live = {}
for ts, name, names, inline in migs:
    srcs = ['Fire3D.Infrastructure/' + res[n] for n in names if n in res]
    if inline:
        srcs.append(inline)
    for p in srcs:
        if not os.path.exists(p):
            continue
        s = open(p, encoding='utf-8-sig').read()
        for fm in re.finditer(r'CREATE (?:OR REPLACE )?FUNCTION (?:public\.)?(\w+)\s*\(', s, re.I):
            live[fm.group(1)] = (ts, name, os.path.relpath(p).replace(chr(92), '/'))
print(len(migs), 'migrations;', len(live), 'functions')
for g in sorted(k for k in live if k.endswith('_gate')):
    print(g, '|', live[g][2], '|', live[g][0] + '_' + live[g][1])
open(sys.argv[2], 'w', encoding='utf-8').write('\n'.join(f'{k}\t{v[2]}\t{v[0]}_{v[1]}' for k, v in sorted(live.items())))

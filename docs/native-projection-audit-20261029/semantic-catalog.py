from pathlib import Path
import collections
import json
import re
import sys

root = Path(__file__).parent
def normalize(value):
    return re.sub(r'\s+', ' ', value.strip())

def catalog(folder):
    entries = collections.defaultdict(set)
    for path in folder.rglob('*.h'):
        relative = path.relative_to(folder).as_posix()
        text = path.read_text()
        if path.name == 'complex_interfaces.h':
            for match in re.finditer(r'    struct __declspec\(novtable\) (\w+) : [^\n]+\n    \{\n(.*?)\n    \};', text, re.S):
                for method in re.finditer(r'^\s*virtual (.*?) __stdcall (\w+)\((.*?)\) noexcept = 0;', match[2], re.M):
                    entries['interface:' + match[1] + '::' + method[2]].add(normalize(method[1] + ' ' + method[2] + '(' + method[3] + ')'))
        if path.parent == folder:
            for method in re.finditer(r'^\s+([^\n;]+?) __stdcall (\w+)\(([^\n]*)\);', text, re.M):
                entries['api:' + path.stem + '::' + method[2]].add(normalize(method[1] + ' ' + method[2] + '(' + method[3] + ')'))
        for match in re.finditer(r'^\s*typedef ([^\n]+?)\(\s*(__\w+)\s*\*\s*(\w+)\)\s*\(([^\n]*)\);', text, re.M):
            entries['callback:' + match[3]].add(normalize(match[1] + ' ' + match[2] + '(' + match[4] + ')'))
        for match in re.finditer(r'^\s*typedef ([^\n;()]+?) (\w+)((?:\[\d+\])*)\s*;', text, re.M):
            entries['alias:' + match[2]].add(normalize(match[1] + match[3]))
        if path.name != 'complex_structs.h':
            continue
        depth = 0
        stack = []
        pending = None
        for line in text.splitlines():
            declaration = re.match(r'\s*(struct|union)\s+(\w+)\s*$', line)
            if declaration:
                pending = declaration[2]
            field = re.match(r'\s{8,}([\w:* ]+?)\s+(\w+)\s*((?:\[\d*\])*)\s*(?::\s*(\d+))?;', line)
            if field and stack and not field[1].startswith(('static_assert', 'return', 'typedef')):
                owner = '::'.join(name for name, level in stack)
                entries['field:' + owner + '::' + field[2]].add(normalize(field[1] + field[3] + (':' + field[4] if field[4] else '')))
            code = re.sub(r'//.*', '', line)
            code = re.sub(r'"(?:[^"\\]|\\.)*"', '""', code)
            opened, closed = code.count('{'), code.count('}')
            if pending and opened:
                stack.append((pending, depth + 1))
                pending = None
            depth += opened - closed
            while stack and stack[-1][1] > depth:
                stack.pop()
    return {key: sorted(value) for key, value in entries.items()}

reference = catalog(root / 'reference')
before = catalog(root / 'before')
target = sys.argv[1] if len(sys.argv) > 1 else 'latest'
after = catalog(root / target / 'win32')
rows = []
for key in sorted(reference.keys() | before.keys() | after.keys()):
    old, previous, current = reference.get(key, []), before.get(key, []), after.get(key, [])
    if old == previous == current:
        continue
    rows.append({'key': key, 'reference': old, 'before': previous, 'after': current,
                 'restored': old == current and old != previous})
output = root / ('semantic-' + target)
output.mkdir(exist_ok=True)
(output / 'catalog-reference.json').write_text(json.dumps(reference, indent=2), encoding='utf-8')
(output / 'catalog-before.json').write_text(json.dumps(before, indent=2), encoding='utf-8')
(output / 'catalog-after.json').write_text(json.dumps(after, indent=2), encoding='utf-8')
(output / 'changes.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
summary = {'reference_declarations': len(reference), 'before_declarations': len(before), 'after_declarations': len(after),
           'changed_declarations': len(rows), 'restored_to_reference': sum(row['restored'] for row in rows),
           'remaining': sum(row['reference'] != row['after'] for row in rows)}
(output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print(summary)
groups = collections.Counter()
for row in rows:
    if row['reference'] == row['after'] or len(row['reference']) != 1 or len(row['after']) != 1:
        continue
    if row['key'].startswith(('api:', 'interface:', 'callback:')):
        continue
    groups[(row['reference'][0], row['after'][0])] += 1
for (old, new), count in groups.most_common(100):
    print(count, old, '=>', new)

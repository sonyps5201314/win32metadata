from pathlib import Path
import collections
import difflib
import hashlib
import json
import re
import subprocess
import sys

root = Path(__file__).parent
reference = root / 'reference'
current_name = sys.argv[1] if len(sys.argv) > 1 else 'before'
current = root / current_name
output = root if current_name == 'before' else root / ('report-' + current_name.replace('/', '-'))
output.mkdir(exist_ok=True)

def inventory(path):
    return {str(f.relative_to(path)): f for f in path.rglob('*') if f.is_file()}

a, b = inventory(reference), inventory(current)
files = []
lines = []
patch = []
changes = collections.Counter()
examples = {}
result = subprocess.run(['git', '-c', 'core.quotePath=false', 'diff', '--no-index', '--no-ext-diff',
    '--src-prefix=reference/', '--dst-prefix=current/', '--unified=0', '--', 'reference', current_name],
    cwd=root, capture_output=True, text=True, encoding='utf-8')
assert result.returncode in (0, 1), result.stderr
item = None
for line in result.stdout.splitlines():
    if line.startswith('+++ '):
        name = line[4:].removeprefix('current/' + current_name + '/')
        files.append(name)
    elif line.startswith('@@ '):
        match = re.match(r'@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@', line)
        item = {'id': len(lines) + 1, 'file': name, 'old_line': int(match[1]), 'new_line': int(match[2]), 'old': [], 'new': []}
        lines.append(item)
    elif line.startswith('-') and not line.startswith('--- '):
        item['old'].append(line[1:])
    elif line.startswith('+') and not line.startswith('+++ '):
        item['new'].append(line[1:])
for item in lines:
        name = item['file']
        if len(item['old']) == len(item['new']):
            for left, right in zip(item['old'], item['new']):
                lt, rt = re.findall(r'\w+|[^\w\s]', left), re.findall(r'\w+|[^\w\s]', right)
                for op, p, q, s, t in difflib.SequenceMatcher(None, lt, rt, autojunk=False).get_opcodes():
                    if op == 'equal':
                        continue
                    key = (' '.join(lt[p:q]), ' '.join(rt[s:t]))
                    changes[key] += 1
                    examples.setdefault(key, {'file': name, 'old': left.strip(), 'new': right.strip()})

(output / 'full-reference-before.diff').write_text(result.stdout, encoding='utf-8')
(output / 'difference-blocks.json').write_text(json.dumps(lines, indent=2), encoding='utf-8')
summary = {'reference_files': len(a), 'current_files': len(b), 'changed_files': files,
           'added_files': sorted(b.keys() - a.keys()), 'removed_files': sorted(a.keys() - b.keys()),
           'difference_blocks': len(lines), 'old_changed_lines': sum(len(v['old']) for v in lines),
           'new_changed_lines': sum(len(v['new']) for v in lines)}
(output / 'inventory.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
pairs = [{'old': key[0], 'new': key[1], 'count': count, 'example': examples[key]} for key, count in changes.most_common()]
(output / 'token-changes.json').write_text(json.dumps(pairs, indent=2), encoding='utf-8')
print({k: v for k, v in summary.items() if k != 'changed_files'})
for item in pairs:
    print(f"{item['count']:4}: {item['old']} -> {item['new']}")

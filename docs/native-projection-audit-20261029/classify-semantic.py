from pathlib import Path
import collections
import json
import re
import sys

root = Path(__file__).parent
tag = sys.argv[1] if len(sys.argv) > 1 else 'latest'
folder = root / ('semantic-' + tag)
rows = json.loads((folder / 'changes.json').read_text())
old = json.loads((folder / 'catalog-reference.json').read_text())
new = json.loads((folder / 'catalog-after.json').read_text())
primitive = {'void': ('void', 0), 'bool': ('bool', 1), 'char': ('i8', 1), 'wchar_t': ('wchar', 2),
 'int8_t': ('i8', 1), 'uint8_t': ('u8', 1), 'int16_t': ('i16', 2), 'uint16_t': ('u16', 2),
 'int32_t': ('i32', 4), 'uint32_t': ('u32', 4), 'int64_t': ('i64', 8), 'uint64_t': ('u64', 8),
 'float': ('f32', 4), 'double': ('f64', 8), 'intptr_t': ('iptr', 8), 'size_t': ('uptr', 8),
 'int': ('i32', 4), 'unsigned int': ('u32', 4)}

def aliases(data):
    return {key[6:]: value[0] for key, value in data.items() if key.startswith('alias:') and len(value) == 1}

def enums(path):
    result = {}
    for file in (path / 'impl').glob('*.h'):
        for match in re.finditer(r'enum class (\w+) : (\w+)', file.read_text()):
            result[match[1]] = match[2]
    return result

old_alias, new_alias = aliases(old), aliases(new)
for key, values in old.items():
    if key.startswith('alias:') and len(values) > 1 and key in new and len(new[key]) == 1 and new[key][0] in values:
        old_alias[key[6:]] = new[key][0]
old_enums, new_enums = enums(root / 'reference'), enums(root / tag / 'win32')
old_callbacks = {key[9:] for key in old if key.startswith('callback:')}
new_callbacks = {key[9:] for key in new if key.startswith('callback:')}

def type_ast(text, table, enum_table, seen=()):
    text = re.sub(r'\b(?:const|volatile|struct|union|enum)\s+', '', text).strip()
    dimensions = re.findall(r'\[(\d*)\]', text)
    text = re.sub(r'\[\d*\]', '', text).strip()
    stars = text.count('*')
    base = text.replace('*', '').strip().split('::')[-1]
    if base in table and base not in seen:
        value = type_ast(table[base], table, enum_table, seen + (base,))
    elif base in primitive:
        value = ('basic', primitive[base][0], primitive[base][1])
    elif base in enum_table:
        value = ('enum', base, primitive.get(enum_table[base], ('?', None))[1])
    elif base in old_callbacks or base in new_callbacks:
        value = ('callback', base, 8)
    else:
        value = ('named', base, {'GUID': 16, 'LARGE_INTEGER': 8, 'ULARGE_INTEGER': 8}.get(base))
    for _ in range(stars):
        value = ('pointer', value)
    for dimension in reversed(dimensions):
        value = ('array', int(dimension) if dimension else None, value)
    return value

def size(value):
    if value[0] == 'pointer': return 8
    if value[0] == 'array':
        inner = size(value[2])
        return inner * value[1] if inner is not None and value[1] is not None else None
    return value[2]

def signature(text, table, enum_table):
    index = text.find('(')
    prefix, params = text[:index], text[index + 1:-1]
    result = [type_ast(prefix.rsplit(' ', 1)[0], table, enum_table)]
    args = []
    depth = 0
    start = 0
    for i, char in enumerate(params + ','):
        if char in '([': depth += 1
        elif char in ')]': depth -= 1
        elif char == ',' and depth == 0:
            part = (params + ',')[start:i].strip()
            start = i + 1
            if not part: continue
            if part == '...': args.append(('vararg', None, None)); continue
            match = re.match(r'(.+?)\s+(\w+)\s*((?:\[\d*\])*)$', part)
            expression = match[1] + match[3] if match else part
            value = type_ast(expression, table, enum_table)
            if value[0] == 'array': value = ('pointer', value[2])
            args.append(value)
    return result + args

summary = collections.Counter()
unresolved = []
for row in rows:
    key, before, after = row['key'], row['reference'], row['after']
    if row['restored']:
        decision = 'restored_reference_sdk_spelling'
    elif before == after:
        decision = 'reference_unchanged'
    elif not before or not after:
        decision = 'declaration_added_or_removed'
    else:
        function = key.startswith(('api:', 'interface:', 'callback:'))
        parser = signature if function else lambda text, a, e: [type_ast(text, a, e)]
        a = [parser(value, old_alias, old_enums) for value in before]
        b = [parser(value, new_alias, new_enums) for value in after]
        if {repr(value) for value in a} == {repr(value) for value in b}:
            decision = 'equivalent_sdk_alias_or_array_syntax'
        elif len(a) == len(b) and all(len(x) == len(y) and all(p == q or size(p) is not None and size(p) == size(q) for p, q in zip(x, y)) for x, y in zip(a, b)):
            decision = 'same_size_requires_sdk_or_mapping_evidence'
        else:
            decision = 'requires_layout_or_signature_review'
            unresolved.append(row)
    row['classification'] = decision
    summary[decision] += 1
(folder / 'classified.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
(folder / 'requires-review.json').write_text(json.dumps(unresolved, indent=2), encoding='utf-8')
(folder / 'classification-summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print(dict(summary))
print('Review rows', len(unresolved))
for row in unresolved[:70]:
    print(row['key'], row['reference'], '=>', row['after'])

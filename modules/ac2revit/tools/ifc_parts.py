"""Memory-bounded IFC subsets for preparation stages.

ifcopenshell keeps the whole model in memory (measured 7-27x the IFC size), so a 2 GB IFC can need 20+ GB.
This module streams the STEP text instead: one pass builds a compact index (offset, type and references per
entity, in numpy arrays), and one more pass writes self-contained parts. A part holds the requested products,
everything they reference (geometry, placements, contexts, units, owner history, the IfcProject) and the inverse
relations the stages read: IfcRelVoidsElement (openings in hosts), IfcRelFillsElement (doors/windows in openings)
and IfcRelAggregates (decomposed parts). Entities are copied byte for byte; nothing is renumbered or recomputed,
so geometry from a part is the geometry of the full file.
"""
import array
import json
import re
from pathlib import Path

import numpy as np

ENTITY = re.compile(rb'#(\d+)\s*=\s*([A-Za-z0-9_]+)\s*\(')
STRING = re.compile(rb"'(?:[^']|'')*'")
REF = re.compile(rb'#(\d+)')
INVERSE = {b'IFCRELVOIDSELEMENT', b'IFCRELFILLSELEMENT', b'IFCRELAGGREGATES'}


def split_arguments(body):
    """Top-level arguments of an entity body (bytes between the outer parentheses)."""
    args, depth, start, i, n = [], 0, 0, 0, len(body)
    while i < n:
        c = body[i]
        if c == 39:  # quote: skip the string, '' is an escaped quote
            i += 1
            while i < n:
                if body[i] == 39:
                    if i + 1 < n and body[i + 1] == 39:
                        i += 2; continue
                    break
                i += 1
        elif c == 40:
            depth += 1
        elif c == 41:
            depth -= 1
        elif c == 44 and depth == 0:
            args.append(body[start:i].strip()); start = i + 1
        i += 1
    args.append(body[start:].strip())
    return args


def product_types(schema):
    import ifcopenshell.ifcopenshell_wrapper as wrapper
    root = wrapper.schema_by_name(schema).declaration_by_name('IfcProduct')
    names, stack = set(), [root]
    while stack:
        d = stack.pop(); names.add(d.name().upper().encode()); stack.extend(d.subtypes())
    return names


def statements(stream):
    """(byte offset, statement bytes) for each DATA entity; tolerates entities split over lines."""
    offset, buffer, start, data = 0, b'', 0, False
    for line in stream:
        here = offset; offset += len(line)
        if not data:
            if line.strip().upper() == b'DATA;':
                data = True
            continue
        if not buffer:
            stripped = line.lstrip()
            if not stripped.startswith(b'#'):
                if stripped.upper().startswith(b'ENDSEC'):
                    return
                continue
            start = here
        buffer += line
        if buffer.rstrip().endswith(b';'):
            yield start, buffer
            buffer = b''


class StepIndex:
    def __init__(self, path):
        self.path = Path(path)
        with self.path.open('rb') as stream:
            head = stream.read(4096)
        match = re.search(rb"FILE_SCHEMA\s*\(\s*\(\s*'([A-Za-z0-9_]+)'", head)
        self.schema = match.group(1).decode() if match else 'IFC2X3'
        products = product_types(self.schema)
        ids, offsets, lengths, codes = array.array('q'), array.array('q'), array.array('l'), array.array('H')
        refs, indptr = array.array('q'), array.array('q', [0])
        self.type_names, type_codes = [], {}
        self.guids, self.tags, self.inverse = {}, {}, []
        self.styled = []  # (IfcStyledItem id, styled geometry item id): colours live on the item, not under it
        with self.path.open('rb') as stream:
            for start, text in statements(stream):
                m = ENTITY.match(text.lstrip())
                if not m:
                    continue
                eid, name = int(m.group(1)), m.group(2).upper()
                code = type_codes.get(name)
                if code is None:
                    code = type_codes[name] = len(self.type_names); self.type_names.append(name)
                body = text.lstrip()[m.end():text.lstrip().rstrip().rfind(b')')]
                plain = STRING.sub(b"''", body)
                ids.append(eid); offsets.append(start); lengths.append(len(text)); codes.append(code)
                refs.extend(int(r) for r in REF.findall(plain)); indptr.append(len(refs))
                if name == b'IFCSTYLEDITEM':
                    args = split_arguments(body)
                    if args and args[0].startswith(b'#'):
                        self.styled.append((eid, int(args[0][1:])))
                if name in products or name in INVERSE:
                    args = split_arguments(body)
                    if name in products:
                        if args and args[0].startswith(b"'"):
                            self.guids[args[0][1:-1].decode('latin-1')] = eid
                        if len(args) > 7 and args[7].startswith(b"'") and len(args[7]) == 38:
                            self.tags[args[7][1:-1].decode('latin-1').upper()] = eid
                    else:
                        relating = int(args[4][1:]) if args[4].startswith(b'#') else 0
                        related = [int(r) for r in REF.findall(STRING.sub(b"''", args[5]))]
                        self.inverse.append((name, eid, relating, related))
        order = np.argsort(np.frombuffer(ids, dtype=np.int64), kind='stable')
        self.ids = np.frombuffer(ids, dtype=np.int64)[order]
        self.offsets = np.frombuffer(offsets, dtype=np.int64)[order]
        self.lengths = np.frombuffer(lengths, dtype=np.int32 if lengths.itemsize == 4 else np.int64)[order]
        self.codes = np.frombuffer(codes, dtype=np.uint16)[order]
        flat, ptr = np.frombuffer(refs, dtype=np.int64), np.frombuffer(indptr, dtype=np.int64)
        starts, ends = ptr[:-1][order], ptr[1:][order]
        counts = ends - starts
        self.indptr = np.concatenate([[0], np.cumsum(counts)])
        gather = np.repeat(starts - self.indptr[:-1], counts) + np.arange(self.indptr[-1])
        self.refs = flat[gather] if len(flat) else flat
        self.project = [int(e) for e, c in zip(self.ids, self.codes) if self.type_names[c] == b'IFCPROJECT']

    def positions(self, entity_ids):
        entity_ids = np.asarray(entity_ids, dtype=np.int64)
        pos = np.searchsorted(self.ids, entity_ids)
        ok = (pos < len(self.ids)) & (self.ids[np.minimum(pos, len(self.ids) - 1)] == entity_ids)
        if not ok.all():
            missing = entity_ids[~ok][:5].tolist()
            raise ValueError(f'IFC references undefined entities: {missing}')
        return pos

    def of_types(self, names):
        wanted = {i for i, n in enumerate(self.type_names) if n in {x.upper().encode() for x in names}}
        return [int(e) for e in self.ids[np.isin(self.codes, list(wanted))]] if wanted else []

    def closure(self, roots):
        """Requested products, their inverse relations and every entity they reference."""
        selected = np.zeros(len(self.ids), dtype=bool)
        by_relating = {}
        for name, rel, relating, related in self.inverse:
            by_relating.setdefault(relating, []).append((name, rel, related))
        by_related = {}
        for name, rel, relating, related in self.inverse:
            for r in related:
                by_related.setdefault(r, []).append((name, rel, relating))
        products = set(int(r) for r in roots) | set(self.project)
        # Inverse relations are followed only between products: openings of a host, the opening of a filling,
        # the host of an opening, the filling of an opening and the parts of an aggregate.
        queue, relations = list(products), set()
        while queue:
            e = queue.pop()
            for name, rel, related in by_relating.get(e, ()):
                if name == b'IFCRELAGGREGATES' and e in self.project:
                    continue  # the spatial tree is not needed and would pull in every storey element
                relations.add(rel)
                for r in related:
                    if r not in products: products.add(r); queue.append(r)
            for name, rel, relating in by_related.get(e, ()):
                if name == b'IFCRELAGGREGATES':
                    continue  # a part does not pull in its whole parent assembly
                relations.add(rel)
                if relating and relating not in products: products.add(relating); queue.append(relating)
        frontier = self.positions(sorted(products | relations))
        while True:
            while len(frontier):
                frontier = frontier[~selected[frontier]]
                if not len(frontier):
                    break
                selected[frontier] = True
                starts, ends = self.indptr[frontier], self.indptr[frontier + 1]
                counts = ends - starts
                if counts.sum() == 0:
                    break
                gather = np.repeat(starts - np.concatenate([[0], np.cumsum(counts)[:-1]]), counts) + np.arange(counts.sum())
                frontier = np.unique(self.positions(np.unique(self.refs[gather])))
            # IfcStyledItem points at the geometry it colours, so it is found from the other side.
            frontier = self._styles_of_selected(selected)
            if not len(frontier):
                break
        return selected

    def _styles_of_selected(self, selected):
        """Style records of selected geometry items that are not selected yet (IfcStyledItem is an inverse reference)."""
        if not self.styled:
            return np.array([], dtype=np.int64)
        if not hasattr(self, '_styled_arrays'):
            self._styled_arrays = (np.array([s for s, _ in self.styled], dtype=np.int64), np.array([i for _, i in self.styled], dtype=np.int64))
        own_ids, item_ids = self._styled_arrays
        last = len(self.ids) - 1
        own = np.minimum(np.searchsorted(self.ids, own_ids), last)
        item = np.minimum(np.searchsorted(self.ids, item_ids), last)
        wanted = (self.ids[own] == own_ids) & (self.ids[item] == item_ids) & selected[item] & ~selected[own]
        return own[wanted]

    def write_parts(self, parts, folder):
        """parts: {name: selected bool mask}. One pass over the source writes every part."""
        folder = Path(folder); folder.mkdir(parents=True, exist_ok=True)
        with self.path.open('rb') as source:
            header = b''
            for line in source:
                header += line
                if line.strip().upper() == b'DATA;':
                    break
        outputs = {name: (folder / (name + '.ifc')).open('wb') for name in parts}
        try:
            for stream in outputs.values():
                stream.write(header)
            masks = list(parts.items())
            with self.path.open('rb') as source:
                for start, text in statements(source):
                    m = ENTITY.match(text.lstrip())
                    if not m:
                        continue
                    pos = int(np.searchsorted(self.ids, int(m.group(1))))
                    for name, mask in masks:
                        if mask[pos]:
                            outputs[name].write(text if text.endswith(b'\n') else text + b'\n')
            for stream in outputs.values():
                stream.write(b'ENDSEC;\nEND-ISO-10303-21;\n')
        finally:
            for stream in outputs.values():
                stream.close()
        return {name: folder / (name + '.ifc') for name in parts}


def ifc_ids(manifest, kinds):
    rows = []
    for e in manifest['elements']:
        if e['kind'] in kinds:
            identifiers = e.get('raw', {}).get('ifcIdentifiers', {})
            rows.append((e['sourceGuid'].upper(), [identifiers.get(f) for f in ('ifcId', 'externalIFCId') if identifiers.get(f)]))
    return rows


def roots(index, manifest, kinds, types=(), limit=None):
    found = []
    for guid, ids in ifc_ids(manifest, kinds)[:limit]:
        entity = next((index.guids[i] for i in ids if i in index.guids), None) or index.tags.get(guid)
        if entity:
            found.append(entity)
    return found + index.of_types(types)


# Stage -> manifest kinds and IFC types. The frame part adds walls for projects without simple columns.
PARTS = {
    'columns': (('Column', 'Wall'), ('IfcGrid',)),
    'beams': (('Beam',), ()),
    'openings': (('Door', 'Window', 'Opening', 'Skylight'), ('IfcOpeningElement',)),
    'walls': (('Wall',), ()),
    'slabs': (('Slab',), ()),
}
SUPPLEMENTAL = ('Roof', 'Mesh', 'Stair', 'Railing', 'CurtainWall', 'Object', 'Shell', 'Skylight', 'Lamp')


def build(manifest_path, ifc_path, folder):
    """Writes every stage part and parts.json (sizes, entity counts, source IFC sha256)."""
    from ac2r.jsonio import file_sha256, write_json
    manifest = json.loads(Path(manifest_path).read_text(encoding='utf-8-sig'))
    index = StepIndex(ifc_path)
    masks, counts = {}, {}
    for name, (kinds, types) in PARTS.items():
        masks[name] = index.closure(roots(index, manifest, kinds, types))
    for kind in SUPPLEMENTAL:
        masks['supplemental-' + kind.lower()] = index.closure(roots(index, manifest, (kind,)))
    files = index.write_parts(masks, folder)
    for name, mask in masks.items():
        counts[name] = dict(file=str(files[name]), entities=int(mask.sum()), bytes=files[name].stat().st_size)
    result = dict(format='ac2r.ifc-parts/1', source=str(ifc_path), sourceSha256=file_sha256(ifc_path),
                  schema=index.schema, entities=int(len(index.ids)), parts=counts)
    write_json(Path(folder) / 'parts.json', result)
    return result


SPLIT_THRESHOLD_BYTES = 300 * 2**20


def should_split(ifc_path):
    """Large IFC files, files that do not fit the memory budget, or AC2R_IFC_PARTS=1 are prepared in parts."""
    import os
    from source_capture import memory_budget
    if os.environ.get('AC2R_IFC_PARTS') == '1':
        return True
    return Path(ifc_path).stat().st_size > SPLIT_THRESHOLD_BYTES or memory_budget(ifc_path)['defer']


def ensure(manifest_path, ifc_path, folder):
    """Existing parts are reused only for the same source IFC (sha256) with every file present."""
    from ac2r.jsonio import file_sha256
    folder = Path(folder); stored = folder / 'parts.json'
    if stored.exists():
        previous = json.loads(stored.read_text(encoding='utf-8'))
        if previous.get('sourceSha256') == file_sha256(ifc_path) and set(previous.get('parts', {})) >= set(PARTS) | {'supplemental-' + k.lower() for k in SUPPLEMENTAL} \
                and all(Path(p['file']).is_file() and Path(p['file']).stat().st_size == p['bytes'] for p in previous['parts'].values()):
            return previous
    print('IFC делится на части по этапам подготовки (потоковое чтение, без загрузки модели целиком)', flush=True)
    return build(manifest_path, ifc_path, folder)


def budget(parts):
    """Memory budget of the largest part, the same rule as source_capture.memory_budget for a whole file."""
    from source_capture import memory_budget
    largest = max(parts['parts'].values(), key=lambda p: p['bytes'])
    result = memory_budget(largest['file'])
    result['largestPart'] = Path(largest['file']).stem
    return result


if __name__ == '__main__':
    import argparse
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'exporter'))
    p = argparse.ArgumentParser(); p.add_argument('--manifest', type=Path, required=True)
    p.add_argument('--ifc', type=Path, required=True); p.add_argument('--out', type=Path, required=True)
    a = p.parse_args()
    print(json.dumps(build(a.manifest, a.ifc, a.out), ensure_ascii=False, indent=2), flush=True)

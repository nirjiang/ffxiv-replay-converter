"""Compose pinned one-to-one routes; unknown revisions use strict fingerprints."""
import json
from pathlib import Path

base = Path(__file__).resolve().parent
data = base / 'data'
profile = json.loads((data / 'profile_v1.0.json').read_text(encoding='utf-8'))
sizes = json.loads((data / 'current_payload_sizes.json').read_text())
catalog = json.loads((data / 'version_catalog.json').read_text())
evidence = json.loads((data / 'historical-evidence.json').read_text())
reference = evidence['diffRevision']
recorded_builds = {'7.51h2': 13592030, '7.55': 13733124, '7.56': 13942280, '7.56h': 14006063}

def read_map(version):
    items = json.loads((data / 'diffs' / (version + '.diff.json')).read_text())
    old_seen, new_seen, result = set(), set(), {}
    for item in items:
        for value in item['old']:
            number = int(value, 16)
            assert number not in old_seen, (version, 'duplicate old', number)
            old_seen.add(number)
        for value in item['new']:
            number = int(value, 16)
            assert number not in new_seen, (version, 'duplicate new', number)
            new_seen.add(number)
        if len(item['old']) == len(item['new']) == 1:
            old, new = int(item['old'][0], 16), int(item['new'][0], 16)
            assert 0 <= old <= 65535 and 0 <= new <= 65535
            result[old] = new
    return result

target_index = next(i for i, v in enumerate(catalog) if v['version_string'] == profile['targetVersion'])
profile['gameVersions'] = {v['version_string']: v['retail_version'] for v in catalog}
profile['targetGameVersion'] = catalog[target_index]['retail_version']
tables = json.loads((data / 'named_tables.json').read_text())
profile['namedOpcodes'] = {p['name']: p['opcode'] for p in next(t for t in tables if t['region'] == 'Global')['lists']['ServerZoneIpcType']}
profile['routes'] = []
for index, entry in enumerate(catalog[:target_index]):
    version = entry['retail_version']
    steps = [v['retail_version'] for v in catalog[index + 1:target_index + 1]]
    combined = read_map(steps[0])
    for step in steps[1:]:
        following = read_map(step)
        combined = {source: following[middle] for source, middle in combined.items() if middle in following}
    inverse = {target: source for source, target in combined.items()}
    assert len(inverse) == len(combined)
    init = inverse[profile['targetInitOpcode']]
    proof = evidence['versions'].get(version)
    named_checks = 0
    sources = [f'https://github.com/xivdev/opcodediff/blob/{reference}/diffs/{s}.diff.json' for s in steps]
    if proof:
        names = proof['serverNames']
        for name, opcode in names.items():
            if name not in profile['namedOpcodes'] or opcode not in combined:
                continue
            assert combined[opcode] == profile['namedOpcodes'][name], (version, name, 'independent conflict')
            named_checks += 1
        assert named_checks >= 20 and names['InitZone'] == init
        sources.append(f"https://github.com/karashiiro/FFXIVOpcodes/blob/{proof['revision']}/opcodes.min.json")
    build = recorded_builds.get(version, 0)
    profile['routes'].append({
        'build': build, 'version': entry['version_string'], 'gameVersion': version, 'initOpcode': init,
        'mapping': {str(s): t for s, t in combined.items()},
        # Current-layout lengths filter compatibility; they are not historical payload evidence.
        'expectedSizes': {str(s): sizes[str(t)] for s, t in combined.items() if str(t) in sizes},
        'steps': steps, 'sources': sources,
        'evidence': 'recording' if build else ('named' if named_checks else 'diff'),
        'namedChecks': named_checks,
    })
    print(version, 'build', build or 'fingerprint', len(combined), 'opcodes;', named_checks, 'independent anchors')
profile['supportedDescription'] = '內建 7.00～7.56 對照（含修正）→ 7.57；較早版本為試轉換'
(base / 'profile.json').write_text(json.dumps(profile, ensure_ascii=False, indent=2), encoding='utf-8')

"""Build strict, composed opcode routes from pinned one-to-one binary diffs."""
import json
from pathlib import Path

base = Path(__file__).resolve().parent
work = base / 'data'
profile = json.loads((work / 'profile_v1.0.json').read_text(encoding='utf-8'))
target_sizes = json.loads((work / 'current_payload_sizes.json').read_text())
reference = 'f6d401145427d672cfad989f3079895388dd1761'
routes = [
    (13592030, '2026.06.18.0000.0000', 0x337, ['7.55', '7.55h', '7.55h2', '7.56', '7.56h', '7.57']),
    (13942280, '2026.09.01.0000.0000', 0x3a1, ['7.56h', '7.57']),
    (14006063, '2026.09.15.0000.0000', 0x32b, ['7.57']),
]

def read_map(version):
    path = work / 'diffs' / (version + '.diff.json')
    data = json.loads(path.read_text())
    result = {}
    for item in data:
        if len(item['old']) != 1 or len(item['new']) != 1:
            continue
        old, new = int(item['old'][0], 16), int(item['new'][0], 16)
        assert old not in result
        result[old] = new
    assert len(set(result.values())) == len(result)
    return result

profile['routes'] = []
catalog = json.loads((work / 'version_catalog.json').read_text())
profile['gameVersions'] = {entry['version_string']: entry['retail_version'] for entry in catalog}
profile['targetGameVersion'] = profile['gameVersions'][profile['targetVersion']]
for build, version, init, steps in routes:
    combined = read_map(steps[0])
    for step in steps[1:]:
        next_map = read_map(step)
        combined = {source: next_map[middle] for source, middle in combined.items() if middle in next_map}
    assert combined[init] == profile['targetInitOpcode']
    assert len(set(combined.values())) == len(combined)
    profile['routes'].append({
        'build': build, 'version': version, 'gameVersion': profile['gameVersions'][version], 'initOpcode': init,
        'mapping': {str(source): target for source, target in combined.items()},
        'expectedSizes': {str(source): target_sizes[str(target)] for source, target in combined.items() if str(target) in target_sizes},
        'steps': steps,
        'sources': ['https://github.com/xivdev/opcodediff/blob/' + reference + '/diffs/' + step + '.diff.json' for step in steps],
    })
    print(build, version, len(combined), 'mapped opcodes;', len(steps), 'steps')
profile['supportedDescription'] = '支援 7.51（修正2）、7.56、7.56（修正1） → 7.57'
tables = json.loads((work / 'named_tables.json').read_text())
profile['namedOpcodes'] = {entry['name']: entry['opcode'] for entry in next(t for t in tables if t['region'] == 'Global')['lists']['ServerZoneIpcType']}
(base / 'profile.json').write_text(json.dumps(profile, ensure_ascii=False, indent=2), encoding='utf-8')

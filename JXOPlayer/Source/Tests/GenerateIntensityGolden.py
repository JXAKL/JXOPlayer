"""Build synthetic parity fixtures using the ES tool's actual grading implementation."""
import importlib.util
import json
import pathlib
import sys
import tempfile

spec = importlib.util.spec_from_file_location('es_intensity', sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
cases = []
for speed in (5, 20, 50, 100, 150, 200, 300, 600, 1000):
    step = 100000 / speed
    cases.append((f'speed-{speed}', {'actions': [{'at': i*step, 'pos': 100*(i % 2)} for i in range(15)]}))
cases.extend([
    ('waits', {'actions': [{'at': 0, 'pos': 0}, {'at': 120000, 'pos': 0}, {'at': 121000, 'pos': 100}, {'at': 122000, 'pos': 0}], 'metadata': {'duration': 400}}),
    ('same-time-same-position', {'actions': [{'at': 0, 'pos': 0}, {'at': 0, 'pos': 0}, {'at': 1000, 'pos': 100}]}),
    ('static', {'actions': [{'at': 0, 'pos': 50}, {'at': 20000, 'pos': 50}]}),
    ('gap-only', {'actions': [{'at': 0, 'pos': 0}, {'at': 20000, 'pos': 100}]}),
    ('out-of-order', {'actions': [{'at': 3000, 'pos': 0}, {'at': 0, 'pos': 0}, {'at': 1000, 'pos': 100}]}),
])
with tempfile.TemporaryDirectory() as directory:
    fixtures = []
    for name, data in cases:
        path = pathlib.Path(directory) / (name + '.funscript')
        path.write_text(json.dumps(data), encoding='utf-8')
        result = module.grade_script(str(path))
        fixtures.append({'name': name, 'data': data, 'expected': result})
    pathlib.Path(sys.argv[2]).write_text(json.dumps(fixtures, ensure_ascii=False, indent=2), encoding='utf-8')

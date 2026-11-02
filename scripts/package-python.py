"""Bundle a relocatable CPython plus the tested IFC environment, without pyvenv.cfg."""
from pathlib import Path
import json
import shutil
import subprocess
import sys

target = Path(sys.argv[1]).resolve()
base = Path(sys.base_prefix)
if target.exists():
    raise FileExistsError(target)
target.mkdir(parents=True)
ignore = shutil.ignore_patterns('__pycache__', '*.pyc', 'site-packages', 'test', 'tests', 'idlelib', 'tkinter', 'ensurepip')
for item in base.iterdir():
    if item.is_file() and (item.suffix.lower() in ('.dll', '.zip') or item.name.lower() in ('python.exe', 'pythonw.exe', 'license.txt')):
        shutil.copy2(item, target / item.name)
for name in ('Lib', 'DLLs'):
    shutil.copytree(base / name, target / name, ignore=ignore)
packages = Path(sys.prefix) / 'Lib' / 'site-packages'
shutil.copytree(packages, target / 'Lib' / 'site-packages',
                ignore=shutil.ignore_patterns('__pycache__', '*.pyc', 'pip', 'pip-*.dist-info', '*.ifc', '*.rvt', '*.pln', '*.bpn'))
result = subprocess.run([str(target / 'python.exe'), '-I', '-B', '-c',
    'import sys,json,ssl,ifcopenshell,numpy,shapely; print(json.dumps(dict(base=sys.base_prefix,ifc=ifcopenshell.version,numpy=numpy.__version__,shapely=shapely.__version__)))'],
    check=True, capture_output=True, text=True)
versions = json.loads(result.stdout)
if Path(versions.pop('base')).resolve() != target:
    raise RuntimeError('Python still depends on its original installation')
(target / 'runtime-versions.json').write_text(json.dumps(versions, indent=2), encoding='utf-8')
print(json.dumps(versions))

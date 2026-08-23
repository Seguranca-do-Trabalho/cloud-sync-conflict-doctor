#!/usr/bin/env python3
"""Extract coverage metrics from coverage.cobertura.xml files."""

import xml.etree.ElementTree as ET
import glob
import sys
import re

files = sorted(glob.glob('tests/Doctor.Tests/TestResults/*/coverage.cobertura.xml'))
if not files:
    print('ERROR: No coverage file found')
    sys.exit(1)

latest = files[-1]
print(f'Coverage file: {latest}')

tree = ET.parse(latest)
root = tree.getroot()

# Get root-level metrics
line_rate = float(root.get('line-rate', 0))
lines_covered = int(root.get('lines-covered', 0))
lines_valid = int(root.get('lines-valid', 0))

print(f'\n=== OVERALL COVERAGE ===')
print(f'Lines covered: {lines_covered}')
print(f'Lines valid: {lines_valid}')
if lines_valid > 0:
    print(f'Line coverage: {lines_covered/lines_valid*100:.1f}%')

# Per-module coverage
modules = {'Doctor.Core': {'covered': 0, 'valid': 0},
           'Doctor.Cli': {'covered': 0, 'valid': 0},
           'Doctor.Gui': {'covered': 0, 'valid': 0}}

for file_elem in root.findall('.//coverage/files/file'):
    path = file_elem.get('path', '')
    lines_elem = file_elem.find('lines')
    if lines_elem is None:
        continue
    
    line_count = int(lines_elem.get('count', 0))
    covered_count = int(lines_elem.get('covered', 0))
    
    if 'Doctor.Core' in path:
        modules['Doctor.Core']['covered'] += covered_count
        modules['Doctor.Core']['valid'] += line_count
    elif 'Doctor.Cli' in path and 'Program.cs' not in path:
        modules['Doctor.Cli']['covered'] += covered_count
        modules['Doctor.Cli']['valid'] += line_count
    elif 'Doctor.Gui' in path and ('.Designer.cs' not in path and '.g.cs' not in path):
        modules['Doctor.Gui']['covered'] += covered_count
        modules['Doctor.Gui']['valid'] += line_count

print(f'\n=== PER-MODULE ===')
for mod, data in modules.items():
    if data['valid'] > 0:
        pct = data['covered']/data['valid']*100
        print(f'{mod}: {data["covered"]}/{data["valid"]} = {pct:.1f}%')
    else:
        print(f'{mod}: no lines found')

print(f'\n=== THRESHOLD CHECK (Core >= 85%) ===')
core = modules['Doctor.Core']
if core['valid'] > 0:
    core_pct = core['covered']/core['valid']*100
    if core_pct >= 85:
        print(f'PASS: Doctor.Core at {core_pct:.1f}% >= 85%')
    else:
        print(f'FAIL: Doctor.Core at {core_pct:.1f}% < 85%')

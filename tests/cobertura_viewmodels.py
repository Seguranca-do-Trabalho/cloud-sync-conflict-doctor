#!/usr/bin/env python3
"""t_2a116a88 - cobertura de linha das classes em ViewModels (piso 60%)."""
import sys
import xml.etree.ElementTree as ET

tree = ET.parse(sys.argv[1])
root = tree.getroot()
tot = 0
hit = 0
for cls in root.iter('class'):
    name = cls.get('name') or ''
    if 'ViewModels' not in name:
        continue
    lines = cls.find('lines')
    if lines is None:
        continue
    line_list = list(lines)
    l = len(line_list)
    h = sum(1 for ln in line_list if int(ln.get('hits') or '0') > 0)
    tot += l
    hit += h
    print(f"{name.rsplit('.', 1)[-1]:28} {h}/{l} = {100 * h / l:.0f}%")
print(f"TOTAL ViewModels: {hit}/{tot} = {100 * hit / tot:.1f}% (piso 60%)")
sys.exit(0 if tot > 0 and hit / tot >= 0.60 else 1)

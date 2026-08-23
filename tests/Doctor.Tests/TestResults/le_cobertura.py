import xml.etree.ElementTree as ET

t = ET.parse('tests/Doctor.Tests/TestResults/0a55cf0c-6cef-416c-8d29-4fa4d7768b14/coverage.cobertura.xml')
root = t.getroot()

# linhas NAO cobertas do OrderedFileEnumerator (identificar lacunas)
for cls in root.iter('class'):
    n = cls.get('name', '')
    if n == 'Doctor.Core.OrderedFileEnumerator':
        for m in cls.iter('method'):
            for ln in m.iter('line'):
                if int(ln.get('hits')) == 0:
                    print("linha nao coberta:", ln.get('number'))

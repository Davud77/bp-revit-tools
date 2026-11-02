import sys,json,zipfile,xml.etree.ElementTree as ET
sys.stdout.reconfigure(encoding='utf-8')
from pathlib import Path
sys.path.insert(0,str(Path('artifacts/python-tools').resolve()))
import xlrd,openpyxl
from pypdf import PdfReader
root=Path('Новая папка');out=Path('artifacts/materials');out.mkdir(parents=True,exist_ok=True)
summary={}
for file in root.iterdir():
    if file.suffix.lower()=='.xls':
        book=xlrd.open_workbook(file);sheets=[]
        for sheet in book.sheets():
            rows=[sheet.row_values(i) for i in range(sheet.nrows)]
            sheets.append({'name':sheet.name,'rows':sheet.nrows,'cols':sheet.ncols,'sample':rows[:18]})
            (out/(file.stem+'-'+sheet.name+'.json')).write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf8')
        summary[file.name]=sheets
    elif file.suffix.lower()=='.xlsx':
        book=openpyxl.load_workbook(file,read_only=True,data_only=False)
        summaries=[]
        for sheet in book:
            rows=[]
            for row in sheet.iter_rows():
                cells=[{'cell':c.coordinate,'value':c.value} for c in row if c.value is not None]
                if cells:rows.append(cells)
            (out/(file.stem+'-'+sheet.title.replace('/','_')+'.json')).write_text(json.dumps(rows,ensure_ascii=False,default=str,indent=2),encoding='utf8')
            summaries.append({'name':sheet.title,'rows':sheet.max_row,'cols':sheet.max_column,'sample':rows[:10]})
        summary[file.name]=summaries
    elif file.suffix.lower()=='.docx':
        with zipfile.ZipFile(file) as z:
            tree=ET.fromstring(z.read('word/document.xml'));ns={'w':'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
            text='\n'.join(''.join(p.itertext()) for p in tree.findall('.//w:p',ns))
            (out/(file.stem+'.txt')).write_text(text,encoding='utf8');summary[file.name]={'text':text[:8500]}
    elif file.suffix.lower()=='.pdf':
        reader=PdfReader(file);text='\n'.join(p.extract_text() or '' for p in reader.pages)
        (out/(file.stem+'.txt')).write_text(text,encoding='utf8');summary[file.name]={'pages':len(reader.pages),'sample':text[:9000]}
    elif file.suffix.lower()=='.json':
        data=json.loads(file.read_text(encoding='utf-8-sig'))
        summary[file.name]={'keys':list(data) if isinstance(data,dict) else 'array','sample':str(data)[:2500]}
(out/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2,default=str),encoding='utf8')
for name,info in summary.items():
    print(name)
    if isinstance(info,list):
        for sheet in info:print(' ',sheet['name'],sheet['rows'],sheet['cols'],str(sheet['sample'])[:3500])
    else:print(str(info)[:4000])

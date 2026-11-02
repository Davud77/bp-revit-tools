import json,hashlib,sys,math,re
from pathlib import Path
sys.path.insert(0,str(Path('artifacts/python-tools').resolve()))
import xlrd
from pypdf import PdfReader
source=Path('Новая папка/Радиаторы керми мощность.xls')
book=xlrd.open_workbook(source)
products=[]
for name in ['X2 Profil','X2 Plan','X2 Line']:
    sheet=book.sheet_by_name(name);current_type=None
    for col in range(3,sheet.ncols):
        if isinstance(sheet.cell_value(12,col),(int,float)):current_type=int(sheet.cell_value(12,col))
        q=sheet.cell_value(11,col);n=sheet.cell_value(10,col);height=sheet.cell_value(13,col)
        if not all(isinstance(v,(int,float)) and v>0 for v in [q,n,height]):continue
        for row in range(17,50,2):
            length=sheet.cell_value(row,1)
            if not isinstance(length,(int,float)) or not isinstance(sheet.cell_value(row-1,col),(int,float)):continue
            products.append(dict(id=f'{name}-{current_type}-{int(height)}-{int(length)}',manufacturer='Kermi',series=name,
                model=f'Kermi {name} {current_type} {int(height)}×{int(length)}',deviceType='panel',mounts=['wall','floor'],
                panelType=str(current_type),heightMm=int(height),lengthMm=int(length),depthMm=100,depthIsSchematic=True,
                qNominal=q*length/1000,n=n,referenceSupply=75,referenceReturn=65,referenceRoom=20,referenceDelta=49.83,
                source=source.name,sheet=name,sourceCells=f'{xlrd.formula.colname(col)}11:{xlrd.formula.colname(col)}14; B{row+1}',
                catalogStatus='provided-table',hygienic=current_type in [10,20,30]))
for name in ['VERTEO-Profil','VERTEO-Plan','VERTEO-Line']:
    sheet=book.sheet_by_name(name)
    for row in range(10,sheet.nrows):
        panel,height,length,n,q=[sheet.cell_value(row,i) for i in [5,6,7,8,9]]
        if not all(isinstance(v,(int,float)) and v>0 for v in [panel,height,length,n,q]):continue
        products.append(dict(id=f'{name}-{int(panel)}-{int(height)}-{int(length)}',manufacturer='Kermi',series=name,
            model=f'Kermi {name} {int(panel)} {int(height)}×{int(length)}',deviceType='vertical',mounts=['vertical'],panelType=str(int(panel)),
            heightMm=int(height),lengthMm=int(length),depthMm=100,depthIsSchematic=True,qNominal=q,n=n,
            referenceSupply=75,referenceReturn=65,referenceRoom=20,referenceDelta=49.83,source=source.name,sheet=name,sourceCells=f'F{row+1}:J{row+1}',catalogStatus='provided-table'))
assert len({p['id'] for p in products})==len(products)
catalog={'schemaVersion':'radiator-catalog-1','source':source.name,'sha256':hashlib.sha256(source.read_bytes()).hexdigest(),
    'note':'Мощности и n из предоставленной таблицы. Актуальность поставки не проверялась. Глубина 100 мм — только условное изображение.',
    'products':products,
    'normative':{'checkedAt':'2026-11-01','current':'СП 60.13330.2020, п. 6.4.6; изменения 1–6',
    'currentUrl':'https://protect.gost.ru/sp/details/b00f766e-b861-4cc7-a448-c65906490262',
    'textUrl':'https://base.garant.ru/400536193/',
                'note':'В действующем п. 6.4.6 нет фиксированных 50/70/75%. Для специальных учреждений длина определяется расчётом и принимается максимально возможной для перекрытия ширины окна. В СП 60.13330.2016 п. 6.4.4 были 50% для жилых/общественных и 75% для перечисленных учреждений; 70% не было. 50% здесь — настраиваемое правило проекта.',
    'clearances':'СП 73.13330.2016 п. 6.4.3: 60 мм от пола, 50 мм до подоконника, 25 мм от стены, если изготовитель не указал иное; для лечебных и детских учреждений 100 мм от пола и 60 мм от стены.',
    'installationUrl':'https://www.abok.ru/pages.php?block=sp73_13330'}}
pdf=Path('Новая папка/Блок 5 теплопотери.pdf');reader=PdfReader(pdf);loads=[];floor=None;current=None
for page,pg in enumerate(reader.pages,1):
    for line in (pg.extract_text() or '').splitlines():
        f=re.match(r'^ЭТАЖ\s*№\s*(\d+)',line)
        if f:floor=int(f[1])
        room=re.match(r'^(\d+)\.\s*(.+)$',line)
        if room:current={'floor':floor,'number':room[1],'name':room[2],'page':page}
        q=re.search(r'∑Qкомн\s*=\s*([\d ]+)\s*Вт',line)
        if q and current:loads.append({**current,'watts':int(q[1].replace(' ',''))});current=None
assert len(loads)==258 and all(r['floor'] is not None and r['watts']>0 for r in loads)
catalog['referenceLoads']={'source':pdf.name,'sha256':hashlib.sha256(pdf.read_bytes()).hexdigest(),'building':'Блок 5','rooms':loads,
    'declaredBuildingWatts':336613,'roundedRoomSumWatts':sum(r['watts'] for r in loads),
    'note':'Привязка к Room выполняется только выбором пользователя: номера и состав помещений исходного PDF и RVT различаются. Сумма округлённых комнат отличается от итога PDF на 16 Вт. В отдельном XLSX-своднике для Блока 5 указано 340014 Вт; эти суммы автоматически не смешиваются.'}
target=Path('HeatLossViewer/Viewer/RadiatorCatalog.json');target.write_text(json.dumps(catalog,ensure_ascii=False,separators=(',',':')),encoding='utf8')
Path('artifacts/materials/catalog-audit.json').write_text(json.dumps({'products':len(products),'source':str(source),'sha256':catalog['sha256'],'series':sorted({p['series'] for p in products})},ensure_ascii=False,indent=2),encoding='utf8')
sys.stdout.reconfigure(encoding='utf8');print(f'{len(products)} позиций; {len(catalog["products"])} записей из проверенных ячеек')

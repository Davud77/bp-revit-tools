import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from prepare_element_hatches import collect,build

PROJECT='3ad37a5f-2516-5366-bba9-e9e336a6492a'
ELEMENT='F906AC1A-168E-4756-B351-74F60B3B4DC8'
FILL='C2FCF76D-525E-401A-B95B-2FD281892719'

class ElementHatchTests(unittest.TestCase):
    def setUp(self):
        self.fill={'use':True,'foregroundPen':1,'backgroundPen':0,'fillId':{'guid':FILL},'use3DHatching':False,
            'orientation':{'type':'Global','origin':{'x':0,'y':0},'matrix00':1,'matrix10':0,'matrix01':0,'matrix11':1}}
        self.manifest={'source':{'projectId':PROJECT},'materials':[],
            'fills':[{'index':7,'attributeId':{'guid':FILL},'name':'Explicit fill','subType':'Vector'}],
            'penTables':[{'isActiveForModel':True,'pens':[{'index':1,'color':{'red':1,'green':0,'blue':0}}]}],
            'elements':[{'sourceGuid':ELEMENT,'kind':'Slab','levelKey':'story:0','raw':{'details':{'floorFill':self.fill}}}]}
        self.target={'projectId':PROJECT,'patterns':[{'name':'Native','fingerprint':'n','geometry':{'target':'Drafting','solid':False}},
            {'name':'Solid','fingerprint':'s','geometry':{'target':'Drafting','solid':True}}]}
        self.config={'rules':{'Explicit fill':'Native'}}
    def test_cover_without_building_material_and_stable_identity(self):
        assignments,issues=build(self.manifest,self.target,self.config)
        self.assertEqual(len(assignments),1);self.assertEqual(issues,[])
        row=assignments[0];self.assertEqual(row['sourceGuid'],ELEMENT);self.assertEqual(row['slot'],'cover')
        self.assertEqual(row['graphics']['foregroundRgb'],[255,0,0]);self.assertEqual(row['graphics']['targetName'],'Native')
        self.assertEqual(build(self.manifest,self.target,self.config)[0],assignments)
        self.assertNotIn('materials',self.target)
    def test_disabled_fill_is_not_applied(self):
        self.fill['use']=False;self.assertEqual(collect(self.manifest),([],[]))
    def test_rotated_or_surface_derived_fill_is_explicitly_unsupported(self):
        self.fill['orientation']['matrix01']=.5
        requests,issues=collect(self.manifest);self.assertEqual(requests,[]);self.assertEqual(len(issues),1)
        self.fill['use3DHatching']=True
        self.assertIn('поверхности',collect(self.manifest)[1][0]['reason'])
    def test_missing_definition_does_not_invent_pattern(self):
        self.manifest['fills']=[];requests,issues=collect(self.manifest)
        self.assertEqual(requests,[]);self.assertEqual(issues[0]['status'],'unsupported')
        self.manifest['penTables']=[]
        rows,issues=build(self.manifest,self.target,self.config)
        self.assertEqual(rows,[]);self.assertEqual(len(issues),1)
    def test_object_override_and_library_internal_attributes_are_distinct(self):
        element=self.manifest['elements'][0];element['kind']='Object'
        details={'sectionFillId':{'guid':FILL},'sectionFillPen':1,'sectionFillBackgroundPen':0,'useObjectSectionAttributes':False}
        element['raw']['details']=details
        rows,issues=build(self.manifest,self.target,self.config);self.assertEqual(rows[0]['slot'],'cut');self.assertEqual(issues,[])
        details['useObjectSectionAttributes']=True
        requests,issues=collect(self.manifest);self.assertEqual(requests,[]);self.assertIn('GDL',issues[0]['reason'])
    def test_unmapped_fill_and_per_element_cut_pens_are_reported(self):
        self.config['rules']={};rows,issues=build(self.manifest,self.target,self.config)
        self.assertEqual(rows,[]);self.assertEqual(issues[0]['status'],'unmapped')
        self.manifest['elements'][0]['raw']['details']['cutFillPen']={'overridden':True,'pen':1}
        self.assertTrue(any(i['slot']=='cutPens' for i in collect(self.manifest)[1]))

if __name__=='__main__':unittest.main()

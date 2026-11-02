"""Read zone vertical constraints through built-in properties on older Tapir versions."""
import math
import re
from .client import aligned, chunks
from .normalize import guid


NAMES = ('General_Height', 'General_TopLinkStory', 'Positioning_TopOffset',
         'Positioning_TopElevationToProjectZero', 'Positioning_BottomElevationToProjectZero')


def constraints(values, home, stories):
    if len(values) != len(NAMES):
        raise ValueError('Incomplete zone constraint property response')
    data = {}
    for name, row in zip(NAMES, values):
        value = row.get('propertyValue', {})
        if value.get('status') != 'normal':
            raise ValueError('Unavailable zone constraint: '+name)
        data[name] = value['value']
    height, top, bottom, offset = (float(data[n]) for n in
        ('General_Height', NAMES[3], NAMES[4], 'Positioning_TopOffset'))
    if not all(math.isfinite(n) for n in (height, top, bottom, offset)) or height <= 0 or abs(top-bottom-height) > 1e-6:
        raise ValueError('Contradictory zone height and absolute elevations')
    result = dict(height=height, bottomOffset=bottom-stories[home]['level'])
    # Archicad's localized story label contains a language-independent +N token.
    # Require both that token and the independent absolute-elevation equation.
    match = re.search(r'\+\s*(\d+)\s*\(', str(data[NAMES[1]]))
    if match:
        relative = int(match.group(1))
        upper = stories.get(home+relative)
        if relative <= 0 or upper is None or abs(upper['level']+offset-top) > 1e-6:
            raise ValueError('Zone upper-story label and elevations disagree')
        result.update(relativeTopStory=relative, topOffset=offset)
    else:
        # Preserve geometric height without claiming an unverified semantic link.
        result['upperRelationVerified'] = False
    return result


def capture(api, snapshot, batch_size=100):
    zones = [e for e in snapshot['elements'] if e.get('type') == 'Zone']
    if not zones:
        return
    properties = api.call('GetPropertyIds', {'properties': [dict(type='BuiltIn', nonLocalizedName=n) for n in NAMES]}, tapir=False)['properties']
    stories = {s['index']: s for s in snapshot['stories']['stories']}
    by_id = {e['sourceGuid']: e for e in zones}
    for batch in chunks([dict(elementId=dict(guid=e['sourceGuid'])) for e in zones], batch_size):
        response = api.call('GetPropertyValuesOfElements', dict(elements=batch, properties=properties), tapir=False)
        for element_id, row in aligned(response, 'propertyValuesForElements', batch):
            source = by_id[guid(element_id)]
            source['verticalConstraintProperties'] = dict(names=NAMES, **row)
            try:
                source['verticalConstraints'] = constraints(row.get('propertyValues', []), source['floorIndex'], stories)
            except (KeyError, TypeError, ValueError) as exc:
                snapshot['warnings'].append('Zone '+source['sourceGuid']+': '+str(exc))

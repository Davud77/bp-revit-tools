"""Per-file review notes and a summary table for the 10-file transfer review (ROADMAP, stage A).

Reads the benchmark outputs copied next to each file (summary.json, pipeline-report.json) and writes
`замечания.md` with automatically found issues plus empty sections for the user, and `сводка.md`.
Existing user notes in `замечания.md` are kept: only the automatic block between markers is replaced.
"""
import argparse
import collections
import json
from pathlib import Path

BEGIN, END = '<!-- авто: начало -->', '<!-- авто: конец -->'
USER_SECTIONS = ['## Не перенеслось', '## Перенеслось неправильно', '## Долго', '## Ошибки и недочёты']
OK = {'created', 'unchanged', 'verified', 'retained', 'planned', 'updated', 'replaced', 'joined', 'geometryVerified', 'complete', 'notice'}


def load(path):
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, ValueError):
        return None


def get(item, name):
    return item.get(name, item.get(name[0].upper() + name[1:]))


def analyse(folder):
    summary = load(folder / 'summary.json') or {}
    report = load(folder / 'pipeline-report.json') or {}
    items = get(report, 'items') or []
    timings = get(report, 'timings') or []
    issues = collections.Counter((get(i, 'kind'), get(i, 'status')) for i in items if get(i, 'status') not in OK)
    examples = collections.defaultdict(list)
    for i in items:
        key = (get(i, 'kind'), get(i, 'status'))
        if key in issues and len(examples[key]) < 3:
            examples[key].append((get(i, 'sourceKey'), (get(i, 'message') or '').splitlines()[0][:220]))
    return summary, items, timings, issues, examples


def auto_block(folder, summary, items, timings, issues, examples):
    lines = [BEGIN, '## Найдено автоматически', '']
    status = summary.get('status', 'нет summary.json')
    lines.append(f"- Результат: **{status}**" + (f" — {summary.get('error')}" if summary.get('error') else ''))
    if summary.get('seconds'):
        lines.append(f"- Время: всего {summary['seconds'] / 60:.1f} мин, сборка в Revit {summary.get('revitSeconds', 0) / 60:.1f} мин; "
                     f"пиковая память Archicad {summary.get('peakPrivateBytes', {}).get('Archicad.exe', 0) / 2**30:.1f} ГиБ, Revit {summary.get('peakPrivateBytes', {}).get('Revit.exe', 0) / 2**30:.1f} ГиБ")
    if summary.get('sourceHashUnchanged') is False:
        lines.append('- ВНИМАНИЕ: исходный PLN изменился во время переноса')
    coverage = summary.get('coverage') or {}
    if coverage:
        lines += ['', '| Категория | В источнике | Перенесено | Нет | Представление |', '|---|---:|---:|---:|---|']
        for kind, c in sorted(coverage.items()):
            rep = ', '.join(f'{k} {v}' for k, v in sorted(c['representations'].items()))
            lines.append(f"| {kind} | {c['source']} | {c['represented']} | {c['missing']} | {rep} |")
    if timings:
        lines += ['', 'Самые долгие этапы Revit: ' + '; '.join(f"{get(t, 'stage')} {get(t, 'seconds'):.0f} с" for t in sorted(timings, key=lambda t: -get(t, 'seconds'))[:5])]
    if issues:
        lines += ['', '| Вид | Статус | Количество | Примеры |', '|---|---|---:|---|']
        for (kind, st), n in sorted(issues.items(), key=lambda x: (-x[1], str(x[0]))):
            ex = '<br>'.join(f'`{k}`: {m}' for k, m in examples[(kind, st)]).replace('|', '/')
            lines.append(f'| {kind} | {st} | {n} | {ex} |')
    site = [i for i in items if str(get(i, 'sourceKey') or '').startswith('site:')]
    if site:
        lines += ['', 'Расположение и единицы: ' + ' '.join(f"[{get(i, 'status')}] {get(i, 'message')}" for i in site)]
    lines += ['', 'Файлы: `model.rvt`, `model.botplus-import.log`, `pipeline-report.json`, `summary.json`, `benchmark.log`.', END]
    return '\n'.join(lines)


def write_notes(folder, block):
    path = folder / 'замечания.md'
    if path.exists():
        text = path.read_text(encoding='utf-8')
        if BEGIN in text and END in text:
            text = text[:text.index(BEGIN)] + block + text[text.index(END) + len(END):]
            path.write_text(text, encoding='utf-8'); return
    head = f'# {folder.name}: замечания\n\nЗаполните разделы ниже: что не перенеслось, что неправильно, что долго, ошибки и недочёты. По возможности укажите GUID, этаж или приложите снимок.\n\n'
    path.write_text(head + block + '\n\n' + '\n\n'.join(s + '\n\n- ' for s in USER_SECTIONS) + '\n', encoding='utf-8')


def main():
    p = argparse.ArgumentParser(); p.add_argument('root', type=Path); a = p.parse_args()
    rows = ['# Перенос 10 тестовых файлов — сводка', '', '| Файл | Результат | Время, мин | Revit, мин | Не перенесено | Замечаний |', '|---|---|---:|---:|---|---:|']
    for folder in sorted(x for x in a.root.iterdir() if x.is_dir()):
        summary, items, timings, issues, examples = analyse(folder)
        write_notes(folder, auto_block(folder, summary, items, timings, issues, examples))
        missing = ', '.join(f"{k} {c['missing']}" for k, c in sorted((summary.get('coverage') or {}).items()) if c['missing'])
        rows.append(f"| [{folder.name}]({folder.name.replace(' ', '%20')}/замечания.md) | {summary.get('status', '—')} | {summary.get('seconds', 0) / 60:.1f} | {summary.get('revitSeconds', 0) / 60:.1f} | {missing or '—'} | {sum(issues.values())} |")
    (a.root / 'сводка.md').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    print('\n'.join(rows))


if __name__ == '__main__':
    main()

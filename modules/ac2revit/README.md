# Ac2Revit

Модуль BotPlus RT для переноса сохранённого PLN из открытого Archicad в Revit. JSON сохраняет исходные GUID, параметры и зависимости. IFC дополняет фактическую геометрию. Поддерживаемые элементы создаются нативными, остальные могут передаваться как DirectShape с отметкой в отчёте.

Установите [BotPlus RT](../../docs/INSTALLATION.md), откройте исходный PLN с Tapir и запустите **BotPlus RT → Ac2Revit → Archicad → Revit**. В [руководстве пользователя](../../USAGE.md#перенос-из-archicad) описаны форматы обмена, выбор категорий и восстановление сеансов.

| Каталог | Назначение |
| --- | --- |
| `revit/Ac2Revit` | Интерфейс, адаптеры, этапы сборки, контрольные точки и отчёты. |
| `revit/assets` | Четыре семейства и шаблона для общей поставки. |
| `exporter/ac2r` | Чтение Archicad через Tapir и нормализация JSON. |
| `tools` | Подготовка IFC, сопоставления и диагностика. |
| `contracts` | JSON-схемы обмена. |
| `config` | Правила сопоставления и примеры конфигураций. |
| `tests` | Автоматические проверки. |

У установленной надстройки сеансы хранятся в `%LOCALAPPDATA%\BotPlus.RT\Ac2Revit\sessions`. Передавайте сеанс целиком, включая `source/raw`, если JSON содержит `gdlReference`. Для стабильного `projectId` при перемещении PLN можно использовать локальный `config/project-registry.local.json` по [примеру](config/project-registry.example.json).

[Архитектура](docs/ARCHITECTURE.md) · [Повторный импорт и восстановление](docs/IMPORT-BEHAVIOR.md) · [Критерии приёмки](docs/VALIDATION.md) · [Дорожная карта](../../ROADMAP.md) · [История проверок](docs/history/README.md)

# Каталог Revit MCP и Code Mode

Срез на 10 ноября 2026 года. Здесь собраны публичные проекты с описанием подключения или инструментов. Основной каталог охватывает MCP для открытой модели Revit, а смежные серверы вынесены отдельно. Версии, возможности и числа сторонних инструментов взяты из материалов разработчиков. У некоторых серверов набор меняется в зависимости от профиля, активного документа или релиза. Строка в каталоге означает, что найден первоисточник. Проверка в текущем Codex и живом Revit проведена для сервера BotPlus RT.

## BP.Revit.AI

BP.Revit.AI использует ключ подключения `bp_revit_ai`. В исходниках зарегистрирован 151 инструмент. Ранее установленный модуль с базовыми 100 командами проверен через Code Mode в Revit 2026. Результаты проверки расширенного набора описаны отдельно. [Аудит реализаций](REVIT-MCP-AUDIT.md) · [Регистрация сервера](../modules/codex-mcp/server/src/index.js) · [Проверки](VALIDATION.md)

<details>
<summary>Полный список из 151 команды MCP</summary>

```text
activate_view
apply_color_by_parameter
apply_configure_schedule
apply_configure_view_filters
apply_connect_mep
apply_copy_documentation_resources
apply_create_3d_views
apply_create_beam_systems
apply_create_beams
apply_create_ceilings
apply_create_dimensions
apply_create_empty_sheets
apply_create_floors
apply_create_grids
apply_create_levels
apply_create_mep
apply_create_plan_views
apply_create_raceways
apply_create_roof
apply_create_room_separators
apply_create_rooms
apply_create_schedule
apply_create_sections
apply_create_sheets
apply_create_walls
apply_delete_plan
apply_disconnect_mep
apply_duplicate_family_types
apply_duplicate_tag_types
apply_duplicate_views
apply_element_graphics
apply_finish_sheet
apply_load_family
apply_merge_mep_tags
apply_parameter_plan
apply_place_family_instances
apply_place_hosted_families
apply_place_sheet_items
apply_position_mep_tags
apply_save_project
apply_save_project_as
apply_set_category_visibility
apply_set_element_worksets
apply_set_view_phase
apply_tag_mep
apply_tag_rooms
apply_tag_walls
apply_transform_elements
apply_update_views
check_element_intersections
export_project_ifc
export_schedule_csv
export_sheets_pdf
export_view_image
export_views_dwg
get_current_view_elements
get_element_properties
get_element_references
get_material_quantities
get_model_warnings
get_project_info
get_project_stats
get_selected_elements
inspect_tag_family
list_annotation_types
list_beam_types
list_documentation_resources
list_dwg_export_setups
list_family_types
list_grids
list_host_types
list_levels
list_link_elements
list_mep_routing_preferences
list_mep_systems
list_mep_types
list_model_links
list_phases
list_plan_view_types
list_project_parameters
list_raceway_types
list_roof_types
list_rooms
list_schedule_categories
list_schedule_fields
list_schedules
list_sheet_resources
list_view_family_types
list_view_filters
list_views
list_worksets
preview_color_by_parameter
preview_configure_schedule
preview_configure_view_filters
preview_connect_mep
preview_copy_documentation_resources
preview_create_3d_views
preview_create_beam_systems
preview_create_beams
preview_create_ceilings
preview_create_dimensions
preview_create_empty_sheets
preview_create_floors
preview_create_grids
preview_create_levels
preview_create_mep
preview_create_plan_views
preview_create_raceways
preview_create_roof
preview_create_room_separators
preview_create_rooms
preview_create_schedule
preview_create_sections
preview_create_sheets
preview_create_walls
preview_delete_elements
preview_disconnect_mep
preview_duplicate_family_types
preview_duplicate_tag_types
preview_duplicate_views
preview_element_graphics
preview_finish_sheet
preview_load_family
preview_merge_mep_tags
preview_place_family_instances
preview_place_hosted_families
preview_place_sheet_items
preview_position_mep_tags
preview_save_project
preview_save_project_as
preview_schedule_fields
preview_set_category_visibility
preview_set_element_worksets
preview_set_parameter
preview_set_view_phase
preview_tag_mep
preview_tag_rooms
preview_tag_walls
preview_transform_elements
preview_update_views
query_stored_data
read_documentation
read_mep_connectors
read_mep_system
read_mep_tag_layout
read_schedule
revit_ping
search_elements
select_elements
store_project_data
store_room_data
```

</details>

## MCP для живой модели Revit

| Проект | Revit по данным автора | Что покрывает и как подключается |
|---|---|---|
| Horizun Revit MCP | 2023–2027 | 125 инструментов по каталогу. Параметрические RFA, фитинги, маршрутизация MEP, листы, проверки и экспорт. Windows-установщик и отдельный MCP. [Каталог](https://github.com/HorizunGroup/horizun-revit-mcp) |
| BIMwright `rvt-mcp` | 2022–2027 | Наборы моделирования, инженерных сетей, чертежей, аннотаций, семейств, спецификаций и экспорта. C#-сервер через stdio. [Каталог](https://github.com/bimwright/rvt-mcp#toolsets) |
| RevitCortex, LuDattilo | 2023–2027 | 173 заявленных инструмента, среди них моделирование, виды, листы, IFC, Power BI и аудит. C#-сервер через stdio, автор публикует конфигурацию Codex. [Репозиторий](https://github.com/LuDattilo/RevitCortex) |
| revit-verify, Daii1414 | 2025–2026 | 259 инструментов в профиле `--toolsets all`. Проверка результатов, DWG, снимки и команды моделирования. Stdio. Выполнение C# отключается отдельным флагом. [Репозиторий](https://github.com/Daii1414/revit-verify) |
| BIM-Bot | 2020–2027 | README описывает 209 инструментов Revit и 58 Navisworks, включая пакетные операции и экспорт. Счётчик в кратком описании GitHub относится к другому снимку проекта. Stdio. [README](https://github.com/HassanElmathary/BIM-Bot) |
| HuskyBIM for Revit | 2024–2027 | Автор заявляет 701 инструмент по элементам, семействам, видам, листам, параметрам, спецификациям и экспорту. Установщик документирует Claude Desktop. Запуск из Codex требует отдельной проверки команды MCP. [Документация](https://mcp.huskybim.com/products/revit) |
| Sparx `mcp-servers-for-revit` | 2020–2026 | Моделирование, запросы, количества и расширяемый C# Command Set. Node MCP через stdio. [Подключение](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) |
| LuDattilo `revit-mcp-server` | 2023–2027 в справочнике команд | Моделирование, листы, размеры, марки стен и помещений, спецификации, аудит. Отдельный репозиторий семейства Sparx. [Команды](https://github.com/LuDattilo/revit-mcp-server/blob/main/COMMANDS.md) |
| AB.RevitMcp | 2020–2026 | 78 инструментов с метрическими аргументами, чтение, создание и изменение модели. Stdio или Streamable HTTP, именованный канал до Revit. [Репозиторий](https://github.com/Al-Qublawi/AB.RevitMcp) |
| `@rui.branco/revit-mcp` | 2025–2027 | 88 инструментов по README, чтение и запись через C#-мост, по одному шагу Undo на запись. Node MCP через stdio. [Инструменты](https://github.com/rui-branco/revit-mcp#tools) |
| `danlion333/revit_mcp` | 2026 проверен автором | pyRevit и Python-stdio. Чтение и запись, в репозитории есть протокол прогона на модели Snowdon Towers. [Проверка и установка](https://github.com/danlion333/revit_mcp) |
| Demolinator Revit MCP | 2024–2027 | 48 инструментов. Моделирование, трубы, воздуховоды, виды, листы и спецификации. Python MCP через pyRevit-мост. [Каталог](https://github.com/Demolinator/revit-mcp-server) |
| KenLP RevitMCPServer | 2025–2027 | Поиск, параметры, виды, марки, PDF, dry run, структурированный список изменений и одна транзакция на пакет. [Репозиторий](https://github.com/KenLP/RevitMCPServer) |
| SHMH Revit MCP | 2023–2027 в README | Запросы и правки открытой модели, C#-сценарии и сохранение рецептов. Документированы локальный Streamable HTTP и stdio. [Релизы и подключение](https://github.com/SHMH-MCPs/revit-mcp-releases) |
| NonicaTab AI Connector | 2020–2027 | Аудит, параметры, отчёты, проверка имён видов и листов. Каталог MCP опубликован отдельно от кнопок NonicaTab. [Инструменты](https://revitmcp.com/tools/) · [Совместимость](https://apps.autodesk.com/RVT/en/Detail/HelpDoc?appId=2476142006549788030&appLang=en&os=Win64) |
| `sharafutdinovdi/revit-model-mcp` | 2022–2027 | Запросы, правки, пакетная обработка моделей и утверждаемые C#-процедуры. MSI, режим чтения, dry run и токены подтверждения. [Инструменты](https://github.com/sharafutdinovdi/revit-model-mcp) |
| BimOnMcp, General-Soju | 2025–2027 | Revit, Dynamo, Navisworks и AutoCAD. Готовые операции, сценарии и предварительный просмотр заданий. Stdio. [README и отчёт автора](https://github.com/General-Soju/BimOnMcp) |
| GEOX3D, zymorel | 2024–2027 по инструкциям сборки | BIM/GIS, Python MCP через stdio и C# add-in. Есть отдельная сборка R26. [Репозиторий](https://github.com/zymorel/revit-mcp) |
| `hungpixi/revit-mcp` | Заявлена поддержка 2026 | C#-мост Revit/Dynamo, запросы геометрии и моделирование. Документирован stdio. [Репозиторий](https://github.com/hungpixi/revit-mcp) |
| `antonhofstader/Revit-2026-MCP-Server` | 2026 | Поиск и выделение, виды, создание объектов и MEP-коннекторов в RFA. [Инструменты](https://github.com/antonhofstader/Revit-2026-MCP-Server) |
| Revit Claude Connector, IbrahimFahdah | 2026 для сборки из исходников | 46 встроенных инструментов по README и расширение собственными пакетами. Stdio с HTTP-мостом до Revit. [Репозиторий](https://github.com/IbrahimFahdah/revit-claude-mcp) |
| REVIT_MCP_study, shuotao | 2022–2026 | Учебный проект с рабочими компонентами и npm-пакетом. Stdio и C# add-in. Авторы документируют ограничение в один активный клиент на мост. [Репозиторий](https://github.com/shuotao/REVIT_MCP_study) |
| RevitMCP, oakplank | Зависит от связки Revit и pyRevit | Расширение pyRevit и Python-сервер для чтения и изменений открытой модели. [Репозиторий](https://github.com/oakplank/RevitMCP) |
| `mcp-server-for-revit-python` | Зависит от связки Revit и pyRevit | Шаблон на pyRevit Routes, 20 инструментов в описанном наборе, включая Python внутри Revit. Stdio. [Репозиторий](https://github.com/mcp-servers-for-revit/mcp-server-for-revit-python) |
| AEC Model Bridge | 2024–2027 заявлены в dev-ветке | Моделирование, листы, спецификации, аннотации, экспорт и отдельное согласование записи. README dev-ветки помечает поведение в живом Revit как непроверенное. [Статус и инструменты](https://github.com/Sam-AEC/aec-model-bridge) |
| Develoh RevitMCP, osc-bouw | В инструкции указан 2025 | MCP размещён в самом add-in. Локальный Streamable HTTP с примером Codex. Наличие сборки 2026 требует проверки. [Репозиторий](https://github.com/osc-bouw/RevitMCP) |
| Autodesk Revit Public MCP Server | Только 2027.2 в Technical Preview | Официальный локальный stdio. Чтение и выбор элементов, экспорт видов, экспериментальные операции записи, планы и спецификации. [Совместимость](https://help.autodesk.com/view/ADSKMCP/ENU/?guid=ADSKMCP_RevitMcp_revit_mcp_server_html) · [Инструменты](https://help.autodesk.com/cloudhelp/ENU/ADSKMCP-RevitMcp/files/revitmcp_tools.html) |

## Смежные проекты и происхождение форков

| Проект | Назначение |
|---|---|
| `shtirlitsDva/cad-mcp` | Один stdio-инструмент запускает C# внутри Revit 2025+. Подходит для изучения механизма выполнения Revit API. Это отдельный слой от JavaScript в Codex. [Репозиторий](https://github.com/shtirlitsDva/cad-mcp) |
| `erp-public` | Узкоспециализированный русскоязычный плагин ЭОМ с MCP-сценариями освещения. Операции ленты и MCP сравниваются отдельно. [Репозиторий](https://github.com/Evgivachev/erp-public) |
| Rvt Docs MCP | Поиск справки и примеров Revit API. Полезен при разработке недостающих инструментов. [Репозиторий](https://github.com/kaitpw/Rvt_Docs_MCP) |
| IfcMCP от IfcOpenShell | Анализ IFC независимо от открытой модели Revit. [Документация](https://docs.ifcopenshell.org/ifcmcp.html) |
| `speckle-mcp`, Daviidro | Проекты, модели и версии в Speckle. Работает с опубликованными данными, которые могут происходить из Revit. [Репозиторий](https://github.com/Daviidro/speckle-mcp) |
| Autodesk APS Revit Automation MCP | Пример серверной обработки облачных моделей через Autodesk Automation API. [Репозиторий Autodesk](https://github.com/autodesk-platform-services/aps-sample-mcp-server-revit-automation) |
| `schauh11/revit-mcp-server` | Нативный чат внутри Revit 2026 и WebSocket. У проекта есть каталог операций, но подключение внешнего Codex к стандартному MCP endpoint требует отдельной проверки. [Репозиторий](https://github.com/schauh11/revit-mcp-server) |
| Оригинальный `revit-mcp` и его `revit-mcp-plugin` / `revit-mcp-commandset` | Архивная база семейства Sparx. Это три компонента одной интеграции. [Архивный репозиторий](https://github.com/mcp-servers-for-revit/revit-mcp) |
| OOXGN RevitMCP | Форк исходного MCP-модуля BotPlus RT. Отдельные пакет, имя сервера и порт, общий исходный набор инструментов. [Сравнение в BotPlus RT](REVIT-MCP-AUDIT.md#ooxgn) |

## Подключение к Code Mode

В настройках этого доверенного проекта включён `features.code_mode.enabled`. Codex помечает функцию как находящуюся в разработке. Для самостоятельного разового запуска можно использовать `codex --enable code_mode`. [Настройки Codex](https://learn.chatgpt.com/docs/config-file/config-reference) · [Настройки проекта](../.codex/config.toml)

Для каждого стороннего кандидата проверка состоит из трёх шагов.

1. Запустить соответствующий add-in для Revit 2026 и проверить стандартный MCP-транспорт сервера. Codex поддерживает stdio и Streamable HTTP. Для локального моста рабочее подключение должно иметь доступ к тому же компьютеру, где открыт Revit. [Документация Codex MCP](https://learn.chatgpt.com/docs/extend/mcp)
2. Получить `tools/list` и проверить имена нужных операций. Настройки `omit_tools_from = ["code_mode"]`, `direct_only_tool_namespaces` и `excluded_tool_namespaces` могут исключить инструменты из JavaScript-вызовов. [Справочник Codex](https://learn.chatgpt.com/docs/config-file/config-reference)
3. Вызвать чтение активной модели из JavaScript. Для изменения модели проверить сценарий на копии RVT через механизм предпросмотра и подтверждения конкретного сервера. Code Mode объединяет вызовы, а границы транзакции определяет Revit add-in.

Для покрытия пробелов BP.Revit.AI первые кандидаты по каталогам — Horizun и BIMwright для инженерных сетей, фитингов и семейств, RevitCortex и BIM-Bot для спецификаций, аудита и экспорта, BimOnMcp для Dynamo и Navisworks. Количество вызовов зависит от того, считают ли авторы предпросмотр отдельно и группируют ли операции в один инструмент с переключателем. Покрытие лучше сверять по конкретным сценариям.

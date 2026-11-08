using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using BotPlus.Openings.Core;
using Grid=System.Windows.Controls.Grid;
using Orientation=System.Windows.Controls.Orientation;

namespace BotPlus.Openings;

internal sealed record RoleChoice(Role Value) { public override string ToString()=>Names.Role(Value); }
internal sealed class ManagerWindow:Window
{
    readonly HoleController controller;
    readonly DataGrid table=MakeTable(),history=MakeTable(),checks=MakeTable(),crossings=MakeTable(),candidates=MakeTable();
    readonly LinkPicker crossingLinkPicker=new("Выберите связи…"),arLinkPicker=new("Выберите модель АР…");
    TabItem crossingsTab=null!,holesTab=null!;
    CrossingOptions rules=HoleSettings.Rules;
    readonly TextBox crossingSection=new(){Text="ОВ",Width=65};
    readonly CheckBox crossingSelected=new(){Content="Только выбранные MEP"};
    readonly ListBox crossingWarnings=new(){MaxHeight=130};
    CrossingOptions? scannedOptions;
    IReadOnlyList<string>? scannedLinks;
    readonly ComboBox targetDocs=new(){MinWidth=260,Margin=new Thickness(0,3,8,3)};
    readonly CheckBox transferApproved=new(){Content="Только согласованные",IsChecked=true,Margin=new Thickness(4,3,12,3),VerticalAlignment=VerticalAlignment.Center};
    IReadOnlyCollection<Autodesk.Revit.DB.ElementId>? scannedSelection;
    readonly TextBlock summary=new(){HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(16,0,0,0)},status=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBox search=new(){Width=220,Margin=new Thickness(8,0,8,0)},comment=new(){MinWidth=300};
    readonly ComboBox state=new(){Width=180,Margin=new Thickness(8,0,0,0)},role=new(){Width=90},links=new(){MinWidth=250};
    readonly CheckBox show3d=new(){Content="Показать в 3D",Margin=new Thickness(10,6,0,0)},showDeleted=new(){Content="Включая удалённые",Margin=new Thickness(10,6,0,0)},approvedOnly=new(){Content="Добавлять только полностью согласованные",IsChecked=true};
    readonly TextBox users=new(){AcceptsReturn=true,MinHeight=105,FontFamily=new FontFamily("Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly TextBox tolerance=new(){Text="0"};
    readonly ComboBox colouring=new(){Width=260,HorizontalAlignment=HorizontalAlignment.Left};
    readonly CheckBox arkr=new(){Content="Разрешить архитектору согласование за КР"};
    Registry data=new();
    readonly TabControl tabs=new();
    internal List<LevelChoice> Levels {get;private set;}=[];
    internal ManagerWindow(HoleController controller)
    {
        this.controller=controller;Title="BotPlus RT — Менеджер отверстий";Width=1250;Height=760;MinWidth=850;MinHeight=540;MaxHeight=SystemParameters.WorkArea.Height-24;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;BotPlusTheme.Apply(this);
        var root=new DockPanel{Margin=new Thickness(10,6,10,0)};Content=root;
        // Заголовка с названием проекта нет: сведения о пользователе и счётчики — в строке состояния.
        var bar=new DockPanel{Margin=new Thickness(0,6,0,0),LastChildFill=true};DockPanel.SetDock(bar,Dock.Bottom);
        var barFrame=new Border{BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(2,5,2,6),Child=bar};barFrame.SetResourceReference(Border.BorderBrushProperty,"BP.Border");DockPanel.SetDock(barFrame,Dock.Bottom);
        cancelButton.Click+=(_,_)=>{cancelRequested=true;cancelButton.IsEnabled=false;};DockPanel.SetDock(cancelButton,Dock.Right);bar.Children.Add(cancelButton);DockPanel.SetDock(progressBar,Dock.Right);bar.Children.Add(progressBar);
        DockPanel.SetDock(summary,Dock.Right);summary.SetResourceReference(TextBlock.ForegroundProperty,"BP.Muted");bar.Children.Add(summary);bar.Children.Add(status);root.Children.Add(barFrame);
        root.Children.Add(tabs);
        var registry=new DockPanel();var filter=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        filter.Children.Add(Button("Обновить",()=>controller.Request((_,_)=>SetStatus("Реестр обновлён."))));filter.Children.Add(search);
        state.ItemsSource=new[]{"Все статусы","Не согласовано","Частично согласовано","Согласовано","Отказ","На удаление","Изменено в модели"};state.SelectedIndex=0;filter.Children.Add(state);filter.Children.Add(showDeleted);filter.Children.Add(show3d);
        DockPanel.SetDock(filter,Dock.Top);registry.Children.Add(filter);
        var menus=new Menu{Margin=new Thickness(0,0,0,8)};
        MenuItem Group(string title){var item=new MenuItem{Header=title};menus.Items.Add(item);return item;}
        void Action(MenuItem menu,string title,Action action){var item=new MenuItem{Header=title};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        var create=Group("Создать");Action(create,"По точке на стене / перекрытии…",()=>controller.Request((ui,r)=>controller.CreateManual(ui,r)));
        Action(create,"Копировать по точке",()=>WithSelected((ui,r,holes)=>controller.CopyPoint(ui,r,One(holes))));
        Action(create,"Копировать по уровням…",()=>WithSelected((_,r,holes)=>CopyLevels(r,holes)));
        var edit=Group("Изменить");Action(edit,"Размеры выбранных (штучно или массово)…",()=>WithSelected((_,r,holes)=>BulkSize(r,holes)));Action(edit,"Положение и размеры…",()=>WithSelected((_,r,holes)=>controller.Edit(r,One(holes))));
        Action(edit,"Объединить выбранные…",()=>WithSelected((_,r,holes)=>
        {
            var answer=MessageBox.Show(this,"Заменить исходные задания объединённым?\nДа — заменить с сохранением их истории; Нет — оставить исходные; Отмена — не объединять.","Объединение отверстий",MessageBoxButton.YesNoCancel);if(answer==MessageBoxResult.Cancel)return;
            var h=HoleWorkflow.Merge(r.Project!,holes,HoleStore.Actor(r),answer==MessageBoxResult.Yes);h.Mark=HoleController.NextMark(r,h.Discipline);r.Holes.Add(h);controller.Save(r,holes.Append(h),"Объединение");
        }));
        var approvals=Group("Согласование");
        Action(approvals,"Согласовать за выбранную роль",()=>Mutate("Согласовать",(r,h)=>HoleWorkflow.Approve(h,HoleStore.Actor(r),SelectedRole())));
        Action(approvals,"Снять своё согласование",()=>Mutate("Снять согласование",(r,h)=>HoleWorkflow.Unapprove(h,HoleStore.Actor(r),SelectedRole())));
        Action(approvals,"Отказать с комментарием",()=>Mutate("Отказ",(r,h)=>HoleWorkflow.Reject(h,HoleStore.Actor(r),Comment())));
        Action(approvals,"Согласование АР за КР",()=>Mutate("Согласовать АР/КР",(r,h)=>HoleWorkflow.ApproveArKr(h,HoleStore.Actor(r),r.Project!,Comment())));
        var deletion=Group("Удаление");Action(deletion,"Запросить удаление",()=>Mutate("Удаление",(r,h)=>HoleWorkflow.RequestDelete(h,HoleStore.Actor(r))));
        Action(deletion,"Разрешить удаление",()=>Mutate("Разрешить удаление",(r,h)=>HoleWorkflow.AllowDelete(h,HoleStore.Actor(r),SelectedRole())));
        Action(deletion,"Отменить запрос удаления",()=>Mutate("Отменить удаление",(r,h)=>HoleWorkflow.CancelDelete(h,HoleStore.Actor(r))));
        var shaft=Group("Шахты");Action(shaft,"Объединить выбранные в шахту",()=>WithSelected((_,r,holes)=>{HoleChecks.SetShaft(holes,HoleStore.Actor(r));controller.Save(r,holes,"Создать шахту");}));
        Action(shaft,"Исключить выбранные из шахты",()=>WithSelected((_,r,holes)=>{HoleChecks.RemoveFromShaft(holes,HoleStore.Actor(r));controller.Save(r,holes,"Изменить шахту");}));
        Action(shaft,"Расформировать шахту выбранного отверстия",()=>WithSelected((_,r,holes)=>{string id=One(holes).ShaftId;if(id.Length==0)throw new InvalidOperationException("Отверстие не входит в шахту.");var members=r.Holes.Where(h=>h.ShaftId==id).ToList();HoleChecks.RemoveFromShaft(members,HoleStore.Actor(r));controller.Save(r,members,"Расформировать шахту");}));
        var cuts=Group("Вырезы");Action(cuts,"Вырезать выбранные согласованные",()=>WithSelected((_,r,holes)=>Cut(r,holes,false)));
        Action(cuts,"Снять вырезы выбранных",()=>WithSelected((_,r,holes)=>Cut(r,holes,true)));
        DockPanel.SetDock(menus,Dock.Top);registry.Children.Add(menus);
        var bottom=new WrapPanel{Margin=new Thickness(0,8,0,0)};bottom.Children.Add(new TextBlock{Text="Роль согласования:",VerticalAlignment=VerticalAlignment.Center});role.ItemsSource=new[]{Role.Engineer,Role.Architect,Role.Structural}.Select(r=>new RoleChoice(r)).ToList();role.SelectedIndex=0;bottom.Children.Add(role);
        bottom.Children.Add(Button("Выбрать в модели",()=>WithSelected((ui,_,holes)=>controller.ShowHole(ui,holes,show3d.IsChecked==true))));
        bottom.Children.Add(Button("Обновить цвета на виде",()=>controller.Request((ui,r)=>HoleStore.InTransaction(controller.Document,"Отверстия — цвета статусов",()=>HoleMarkers.Colour(controller.Document,ui.ActiveUIDocument.ActiveView,r.Holes,SelectedRole())))));
        DockPanel.SetDock(bottom,Dock.Bottom);registry.Children.Add(bottom);
        AddColumns(table,("Марка","Mark"),("Раздел","Discipline"),("Задание","Assignment"),("Уровень","LevelName"),("Размер, мм","Size"),("Основа","OrientationText"),("Статус","Status"),("Согласования","ApprovalText"),("Вырез","CutStatus"),("Ревизия","Revision"),("Центр Z, мм","Geometry.Z"),("Низ, мм","Bottom"),("Шахта","ShaftText"));
        registry.Children.Add(table);
        // Вкладка «Пересечения»: изучение и поиск коллизий, ничего не создаёт.
        var crossingPanel=new DockPanel();var crossingControls=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        void Option(WrapPanel bar,string label,Control input){bar.Children.Add(Caption(label));input.Margin=new Thickness(0,3,8,3);bar.Children.Add(input);}
        crossingControls.Children.Add(Caption("Связи для поиска"));crossingControls.Children.Add(crossingLinkPicker);
        foreach(var box in new Control[]{crossingSelected}){box.Margin=new Thickness(4,3,12,3);box.VerticalAlignment=VerticalAlignment.Center;crossingControls.Children.Add(box);}
        crossingControls.Children.Add(Button("Найти пересечения",()=>Search(crossingLinkPicker)));
        DockPanel.SetDock(crossingControls,Dock.Top);crossingPanel.Children.Add(crossingControls);
        var warningPanel=new Expander{Header="Замечания поиска",Content=crossingWarnings,IsExpanded=true,Margin=new Thickness(0,8,0,0)};DockPanel.SetDock(warningPanel,Dock.Bottom);crossingPanel.Children.Add(warningPanel);
        AddColumns(crossings,("Тип","Kind"),("Состояние","State"),("Коммуникация","Communication"),("Основа","Host"),("Уровень","LevelName"),("Размер, мм","Size"),("Глубина, мм","Depth"),("X, мм","Geometry.X"),("Y, мм","Geometry.Y"),("Z, мм","Geometry.Z"));
        crossingPanel.Children.Add(crossings);crossingsTab=AddTab("Пересечения",crossingPanel);
        // Вкладка «Отверстия»: выбор модели АР, создание заданий из найденных пересечений и реестр.
        var holesPanel=new Grid();
        holesPanel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});holesPanel.RowDefinitions.Add(new RowDefinition{Height=new GridLength(2,GridUnitType.Star),MinHeight=90});
        holesPanel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});holesPanel.RowDefinitions.Add(new RowDefinition{Height=new GridLength(3,GridUnitType.Star),MinHeight=160});
        var createBar=new WrapPanel{Margin=new Thickness(0,0,0,6)};
        createBar.Children.Add(Caption("Модель АР (стены)"));createBar.Children.Add(arLinkPicker);
        Option(createBar,"Раздел",crossingSection);
        createBar.Children.Add(Button("Найти пересечения",()=>Search(arLinkPicker)));
        createBar.Children.Add(Button("Правила",()=>{var edited=RulesWindow.Edit(this,rules);if(edited==null)return;rules=edited;HoleSettings.Rules=edited;SetStatus("Правила сохранены. Повторите поиск пересечений: размеры пересчитаются по новым правилам.");}));
        createBar.Children.Add(Button("Создать выбранные задания",()=>
        {
            var chosen=candidates.SelectedItems.OfType<CrossingCandidate>().Concat(candidates.SelectedItems.Count==0?crossings.SelectedItems.OfType<CrossingCandidate>():[]).ToList();string discipline=crossingSection.Text.Trim();
            if(scannedOptions==null||scannedOptions!=ReadCrossingOptions())throw new InvalidOperationException("Сначала обновите поиск на вкладке «Пересечения» с текущими параметрами.");
            var options=scannedOptions;var selection=scannedSelection;var order=scannedLinks;var architect=arLinkPicker.SelectedIds;
            if(chosen.Count==0)throw new InvalidOperationException("Выберите строки со статусом «Новое задание».");
            controller.Request((_,r)=>
            {
                List<Hole> created;cancelRequested=false;cancelButton.IsEnabled=true;
                try
                {
                    created=HoleCrossings.Create(controller.Document,r,options,selection,chosen,discipline,order,architect,Progress,()=>cancelRequested);
                    controller.Save(r,created,"Создать задания по пересечениям");
                }
                catch(OperationCanceledException){EndProgress();SetStatus("Создание отменено, задания не созданы.");return;}
                // Список остаётся на месте и обновляется: строки с созданными заданиями получают новое состояние.
                try
                {
                    var found=HoleCrossings.Scan(controller.Document,r,options,selection,order,Progress,()=>cancelRequested);
                    crossings.ItemsSource=found.Candidates;candidates.ItemsSource=found.Candidates;crossingWarnings.ItemsSource=found.Warnings;
                    SetStatus($"Создано заданий: {created.Count}. Список обновлён: новых заданий {found.Candidates.Count(c=>c.CanCreate)} из {found.Candidates.Count} пересечений. Согласования и физические вырезы выполняются отдельно.");
                }
                catch(OperationCanceledException){SetStatus($"Создано заданий: {created.Count}. Обновление списка отменено: повторите поиск.");}
                finally{EndProgress();}
            });
        }));
        var transferBar=new WrapPanel{Margin=new Thickness(0,0,0,6)};
        transferBar.Children.Add(Caption("Сводный файл"));transferBar.Children.Add(targetDocs);transferBar.Children.Add(transferApproved);
        transferBar.Children.Add(Button("Перенести в сводный файл",()=>
        {
            if(targetDocs.SelectedItem is not LinkChoice target)throw new InvalidOperationException("Откройте сводный файл Revit и нажмите «Обновить», затем выберите его в списке.");
            var keys=table.SelectedItems.OfType<Hole>().Select(h=>h.Key).ToHashSet();bool approved=transferApproved.IsChecked==true;
            controller.Request((_,r)=>
            {
                HoleController.RequireProject(r);var result=HoleTransfer.Run(controller.Document,HoleTransfer.Find(controller.Document,target.Id),r,r.Holes.Where(h=>keys.Count==0||keys.Contains(h.Key)).ToList(),approved);
                SetStatus(result.Text);
            });
        }));
        var top=new StackPanel();top.Children.Add(createBar);
        Grid.SetRow(top,0);holesPanel.Children.Add(top);
        AddColumns(candidates,("Тип","Kind"),("Состояние","State"),("Коммуникация","Communication"),("Основа","Host"),("Уровень","LevelName"),("Размер, мм","Size"),("Глубина, мм","Depth"));
        Grid.SetRow(candidates,1);holesPanel.Children.Add(candidates);
        var splitter=new GridSplitter{Height=5,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,3,0,3)};Grid.SetRow(splitter,2);holesPanel.Children.Add(splitter);
        Grid.SetRow(registry,3);holesPanel.Children.Add(registry);holesTab=AddTab("Отверстия",holesPanel);
        var historyPanel=new DockPanel();var commentRow=new WrapPanel{Margin=new Thickness(0,0,0,10)};commentRow.Children.Add(comment);commentRow.Children.Add(Button("Добавить комментарий",()=>Mutate("Комментарий",(r,h)=>HoleWorkflow.Comment(h,HoleStore.Actor(r),Comment()))));
        commentRow.Children.Add(Button("Архивировать прежнюю историю",()=>Mutate("Архивировать историю",(r,h)=>HoleWorkflow.ArchiveHistory(h,HoleStore.Actor(r)))));
        DockPanel.SetDock(commentRow,Dock.Top);historyPanel.Children.Add(commentRow);AddColumns(history,("Дата","TimeText"),("Пользователь","User"),("Роль","RoleText"),("Действие","Action"),("Комментарий","Comment"),("Ревизия","Revision"));historyPanel.Children.Add(history);var historyTab=AddTab("История",historyPanel);
        var checkPanel=new DockPanel();var checkButtons=new WrapPanel{Margin=new Thickness(0,0,0,10)};
        checkButtons.Children.Add(Button("Проверить расстояния…",()=>controller.Request((_,r)=>
        {var text=Ask("Минимальные расстояния X; Y; Z в мм","50;50;50");if(text==null)return;var v=text.Split(';').Select(Parse).ToArray();if(v.Length!=3)throw new InvalidOperationException("Введите три расстояния через точку с запятой.");checks.ItemsSource=HoleChecks.Spacing(r.Holes,new(v[0],v[1],v[2]));SetStatus("Проверка по габаритам завершена. Для повёрнутых отверстий уточните зазор в 3D.");})));
        checkButtons.Children.Add(Button("Показать пару",()=>{if(checks.SelectedItem is HoleIssue issue){var keys=new[]{issue.First,issue.Second};controller.Request((ui,r)=>controller.ShowHole(ui,r.Holes.Where(h=>keys.Contains(h.Key)).ToList(),true));}}));
        checkButtons.Children.Add(Button("Экспорт CSV",()=>ExportCsv(checks)));DockPanel.SetDock(checkButtons,Dock.Top);checkPanel.Children.Add(checkButtons);
        AddColumns(checks,("Первое отверстие","First"),("Второе отверстие","Second"),("Уровень","Level"),("Результат","Message"));checkPanel.Children.Add(checks);AddTab("Проверки",checkPanel);
        var exchange=new StackPanel{Margin=new Thickness(16)};exchange.Children.Add(new TextBlock{Text="Обмен заданиями",FontSize=19});exchange.Children.Add(new TextBlock{Text="Из связи используется её фактическое преобразование координат. JSON передаёт внутренние координаты в миллиметрах.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,14)});
        exchange.Children.Add(links);exchange.Children.Add(approvedOnly);exchange.Children.Add(Button("Обновить из выбранной связи",()=>controller.Request((_,r)=>ImportLink(r))));
        exchange.Children.Add(Button("Импорт JSON…",()=>{var dialog=new OpenFileDialog{Filter="Задания отверстий|*.json"};if(dialog.ShowDialog(this)==true){string file=dialog.FileName;controller.Request((_,r)=>Import(r,HoleJson.Read<HolePackage>(File.ReadAllText(file))));}}));
        exchange.Children.Add(Button("Экспорт исходного реестра JSON…",()=>{var dialog=new SaveFileDialog{Filter="Задания отверстий|*.json",FileName="Отверстия.json"};if(dialog.ShowDialog(this)==true){string file=dialog.FileName;controller.Request((_,r)=>{HoleController.RequireProject(r);File.WriteAllText(file,HoleJson.Write(HoleExchange.Export(r.Project!,r.Holes)));SetStatus("Задания экспортированы: "+file);});}}));exchange.Children.Add(new TextBlock{Text="Сводный файл",FontSize=15,Margin=new Thickness(0,18,0,4)});exchange.Children.Add(new TextBlock{Text="Тела отверстий строятся в этом файле и копируются в открытый сводный файл Revit. Повторный перенос заменяет прежние копии без дублей.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});exchange.Children.Add(transferBar);var exchangeTab=AddTab("Обмен",exchange);
        var settings=new StackPanel{Margin=new Thickness(16)};settings.Children.Add(new TextBlock{Text="Настройки реестра",FontSize=19});settings.Children.Add(Button("Создать реестр и назначить себя первым администратором",()=>controller.Request((_,r)=>controller.Initialize(r))));
        settings.Children.Add(new TextBlock{Text="Пользователи: имя Windows=Инженер / Архитектор / Конструктор / Админ;",Margin=new Thickness(0,16,0,6)});settings.Children.Add(users);
        settings.Children.Add(Button("Прочитать список пользователей…",()=>{var dialog=new OpenFileDialog{Filter="Список пользователей|*.txt"};if(dialog.ShowDialog(this)==true)users.Text=File.ReadAllText(dialog.FileName);}));
        settings.Children.Add(new TextBlock{Text="Допуск перемещений и размеров (мм, для всех четырёх типов):",Margin=new Thickness(0,14,0,6)});settings.Children.Add(tolerance);settings.Children.Add(arkr);
        settings.Children.Add(new TextBlock{Text="Раскраска отверстий на виде",Margin=new Thickness(0,18,0,6)});colouring.ItemsSource=new[]{"По разделу (по умолчанию)","По статусу"};colouring.SelectedIndex=HoleSettings.Colouring==ColouringMode.Status?1:0;settings.Children.Add(colouring);
        colouring.SelectionChanged+=(_,_)=>{HoleSettings.Colouring=colouring.SelectedIndex==1?ColouringMode.Status:ColouringMode.Discipline;controller.Request((ui,r)=>{HoleStore.InTransaction(controller.Document,"Отверстия — раскраска",()=>HoleMarkers.Colour(controller.Document,ui.ActiveUIDocument.ActiveView,r.Holes,SelectedRole()));SetStatus("Раскраска применена к текущему виду.");});};
        settings.Children.Add(Button("Сохранить настройки",()=>controller.Request((_,r)=>SaveSettings(r))));AddTab("Настройки",new ScrollViewer{Content=settings,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        {int i=tabs.Items.IndexOf(historyTab),j=tabs.Items.IndexOf(exchangeTab);tabs.Items.Remove(historyTab);tabs.Items.Remove(exchangeTab);tabs.Items.Insert(i,exchangeTab);tabs.Items.Insert(j,historyTab);}
        crossings.PreviewMouseLeftButtonUp+=(_,e)=>GoTo(e);candidates.PreviewMouseLeftButtonUp+=(_,e)=>GoTo(e);
        foreach(var grid in new[]{crossings,candidates})grid.RowStyle=RowColours();
        search.TextChanged+=(_,_)=>Filter();state.SelectionChanged+=(_,_)=>Filter();showDeleted.Checked+=(_,_)=>Filter();showDeleted.Unchecked+=(_,_)=>Filter();
        table.SelectionChanged+=(_,_)=>UpdateHistory();table.MouseDoubleClick+=(_,_)=>{if(table.SelectedItem is Hole)WithSelected((ui,_,holes)=>controller.ShowHole(ui,holes,show3d.IsChecked==true));};
        SetStatus("Создайте или откройте реестр. Изменения сохраняются в RVT при обычном сохранении проекта.");
    }
    void Search(LinkPicker picker)
    {

        var ordered=picker.SelectedIds;SetStatus("Идёт поиск пересечений…");
        controller.Request((ui,r)=>
        {
                var options=ReadCrossingOptions();var selected=crossingSelected.IsChecked==true?ui.ActiveUIDocument.Selection.GetElementIds().ToArray():null;
                var order=ordered;
                CrossingScan found;cancelRequested=false;cancelButton.IsEnabled=true;
                try{found=HoleCrossings.Scan(controller.Document,r,options,selected,order,Progress,()=>cancelRequested);}
                catch(OperationCanceledException){EndProgress();SetStatus("Поиск отменён.");return;}
                finally{EndProgress();}
                scannedOptions=options;scannedSelection=selected;scannedLinks=order;
                crossings.ItemsSource=found.Candidates;candidates.ItemsSource=found.Candidates;crossingWarnings.ItemsSource=found.Warnings;
                SetStatus((found.Candidates.Count==0?"Пересечений не найдено; смотрите «Замечания поиска» ниже. ":"")+$"Коммуникаций: {found.Communications}; основ: {found.HostCount}; пересечений: {found.Candidates.Count}; новых заданий: {found.Candidates.Count(c=>c.CanCreate)}; замечаний: {found.Warnings.Count}. Поиск не изменяет модель. Всегда проверяется открытая модель, затем выбранные связи по порядку. Строки «Сеть × сеть» только показывают коллизии.");
        });
    }
    /// <summary>Клик по ячейке «Коммуникация» выбирает элемент текущей модели, по «Основа» — элемент связи, по остальным — оба.</summary>
    void GoTo(System.Windows.Input.MouseButtonEventArgs e)
    {
        var node=e.OriginalSource as DependencyObject;DataGridCell? cell=null;
        while(node!=null)
        {
            if(node is DataGridCell found){cell=found;break;}
            node=node is Visual||node is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);
        }
        if(cell?.DataContext is not CrossingCandidate candidate)return;
        var mode=cell.Column.Header as string switch{"Коммуникация"=>CrossingTarget.Communication,"Основа"=>CrossingTarget.Host,_=>CrossingTarget.Both};
        controller.Request((ui,_)=>controller.ShowCrossing(ui,candidate,mode));
    }
    /// <summary>Строки, для которых тело отверстия уже создано, подкрашены; выделение остаётся заметным.</summary>
    static Style RowColours()
    {
        var style=new Style(typeof(DataGridRow));
        var done=new DataTrigger{Binding=new Binding(nameof(CrossingCandidate.HasBody)),Value=true};done.Setters.Add(new Setter(Control.BackgroundProperty,new System.Windows.DynamicResourceExtension("BP.Done")));style.Triggers.Add(done);
        var selected=new Trigger{Property=DataGridRow.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new System.Windows.DynamicResourceExtension("BP.Selected")));style.Triggers.Add(selected);
        return style;
    }
    static DataGrid MakeTable()=>new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserSortColumns=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0};
    static void AddColumns(DataGrid grid,params (string Header,string Path)[] columns)
    {
        foreach(var c in columns)
        {
            var binding=new Binding(c.Path);
            if(c.Path.StartsWith("Geometry.",StringComparison.Ordinal)||c.Path is "Bottom" or "Depth")binding.StringFormat="{0:0.#}";
            grid.Columns.Add(new DataGridTextColumn{Header=c.Header,Binding=binding,SortMemberPath=c.Path,MinWidth=70,Width=new DataGridLength(1,DataGridLengthUnitType.Auto)});
        }
    }
    static Button Button(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(12,4,12,4),Margin=new Thickness(0,3,8,3),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=(_,_)=>{try{action();}catch(Exception error){MessageBox.Show(error.Message,"Менеджер отверстий");}};return b;}
    TabItem AddTab(string title,object content){var tab=new TabItem{Header=title,Content=content};tabs.Items.Add(tab);return tab;}
    static TextBlock Caption(string text)=>new(){Text=text,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0),Opacity=0.85};
    internal void SelectHoles(HashSet<string> keys)
    {
        var rows=table.Items.OfType<Hole>().Where(h=>keys.Contains(h.Key)).ToList();if(rows.Count==0)return;
        tabs.SelectedItem=holesTab;table.SelectedItems.Clear();foreach(var row in rows)table.SelectedItems.Add(row);table.ScrollIntoView(rows[0]);
        SetStatus($"Выбрано в Revit отверстий: {rows.Count}.");
    }
    internal void SetTargets(List<LinkChoice> targets)
    {var old=(targetDocs.SelectedItem as LinkChoice)?.Id;targetDocs.ItemsSource=targets;targetDocs.SelectedItem=targets.FirstOrDefault(t=>t.Id==old)??targets.FirstOrDefault();}
    internal void ShowTab(bool crossingsFirst){tabs.SelectedItem=crossingsFirst?crossingsTab:holesTab;}
    bool cancelRequested;
    readonly ProgressBar progressBar=new(){Width=220,Height=14,Minimum=0,Maximum=1,Visibility=Visibility.Collapsed,Margin=new Thickness(12,0,0,0)};
    readonly Button cancelButton=new(){Content="Отмена",Padding=new Thickness(10,1,10,1),Margin=new Thickness(8,0,0,0),Visibility=Visibility.Collapsed};
    internal void SetStatus(string text)=>status.Text=text;
    void Progress(string text,double fraction)
    {
        status.Text=text;progressBar.IsIndeterminate=fraction<0;if(fraction>=0)progressBar.Value=Math.Min(1,fraction);
        progressBar.Visibility=Visibility.Visible;cancelButton.Visibility=Visibility.Visible;
        Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Background);
    }
    void EndProgress(){progressBar.Visibility=Visibility.Collapsed;cancelButton.Visibility=Visibility.Collapsed;}
    internal void SetData(Registry registry,List<LevelChoice> levels,List<LinkChoice> linked,string title)
    {
        data=registry;Levels=levels;crossingLinkPicker.SetItems(linked);arLinkPicker.SetItems(linked);var actor=HoleStore.Actor(data);
        summary.Text=$"{actor.User} · {Names.Role(actor.Role)}     Всего: {data.Holes.Count(h=>!h.Deleted)} · Согласовано: {data.Holes.Count(h=>h.FullyApproved)} · На удаление: {data.Holes.Count(h=>h.DeletionRequested)}";
        role.IsEnabled=actor.Role==Role.Admin;if(actor.Role is Role.Engineer or Role.Architect or Role.Structural)role.SelectedItem=role.Items.OfType<RoleChoice>().First(r=>r.Value==actor.Role);
        var oldLink=(links.SelectedItem as LinkChoice)?.Id;links.ItemsSource=linked;links.SelectedItem=linked.FirstOrDefault(l=>l.Id==oldLink)??linked.FirstOrDefault();
        if(data.Project!=null){users.Text=string.Join(Environment.NewLine,data.Project.Users.Select(p=>p.Key+"="+p.Value+";"));tolerance.Text=data.Project.ToleranceFor(new HoleGeometry()).X.ToString(CultureInfo.CurrentCulture);arkr.IsChecked=data.Project.AllowArchitectStructuralApproval;}
        Filter();
    }
    void Filter()
    {
        var selected=table.SelectedItems.OfType<Hole>().Select(h=>h.Key).ToHashSet();string text=search.Text.Trim();string selectedStatus=state.SelectedItem as string??"Все статусы";
        table.ItemsSource=data.Holes.Where(h=>(showDeleted.IsChecked==true||!h.Deleted)&&(selectedStatus=="Все статусы"||h.Status==selectedStatus)
            &&(text.Length==0||string.Join(" ",h.Mark,h.Discipline,h.Assignment,h.LevelName,h.Id,h.Status,h.Size).Contains(text,StringComparison.OrdinalIgnoreCase))).OrderBy(h=>h.LevelElevation).ThenBy(h=>h.Mark).ToList();
        foreach(var h in table.Items.OfType<Hole>())if(selected.Contains(h.Key))table.SelectedItems.Add(h);UpdateHistory();
    }
    void UpdateHistory(){history.ItemsSource=table.SelectedItem is Hole h?h.History.Skip(h.HistoryStart).OrderByDescending(e=>e.Time).ToList():null;}
    Role SelectedRole()=>HoleStore.Actor(data).Role==Role.Admin&&role.SelectedItem is RoleChoice r?r.Value:HoleStore.Actor(data).Role;
    internal Role ApprovalRole=>SelectedRole();
    CrossingOptions ReadCrossingOptions(){rules.Validate();return rules with{IncludeLinks=true};}
    string Comment()=>comment.Text.Trim();
    static Hole One(IReadOnlyList<Hole> holes)=>holes.Count==1?holes[0]:throw new InvalidOperationException("Выберите одно отверстие.");
    void WithSelected(Action<Autodesk.Revit.UI.UIApplication,Registry,List<Hole>> action)
    {
        var keys=table.SelectedItems.OfType<Hole>().Select(h=>h.Key).ToHashSet();
        if(keys.Count==0){SetStatus("Выберите строки отверстий в реестре.");return;}
        controller.Request((ui,r)=>{HoleController.RequireProject(r);action(ui,r,r.Holes.Where(h=>keys.Contains(h.Key)).ToList());});
    }
    void Mutate(string title,Action<Registry,Hole> action)=>WithSelected((_,r,holes)=>{foreach(var h in holes)action(r,h);controller.Save(r,holes,title);SetStatus(title+": "+holes.Count);});
    void Cut(Registry registry,List<Hole> holes,bool remove)
    {
        if(HoleStore.Actor(registry).Role is not (Role.Architect or Role.Structural or Role.Admin))throw new InvalidOperationException("Вырезание выполняют АР, КР или администратор.");
        SetStatus(string.Join(Environment.NewLine,HoleCuts.Run(controller.Document,registry,holes,remove)));
    }
    void BulkSize(Registry r,List<Hole> holes)
    {
        var edit=BulkSizeWindow.Ask(this,holes.Count);if(edit==null)return;
        var actor=HoleStore.Actor(r);var changed=new List<Hole>();var skipped=new List<string>();
        double Next(double? value,double current)=>value==null?current:edit.Add?current+value.Value:value.Value;
        foreach(var h in holes)
        {
            try
            {
                var g=h.Geometry;bool round=g.Shape==HoleShape.Round;
                g=g with{Width=round?g.Width:Next(edit.Width,g.Width),Height=round?g.Height:Next(edit.Height,g.Height),Diameter=round?Next(edit.Diameter,g.Diameter):g.Diameter,Depth=Next(edit.Depth,g.Depth)};
                g.Validate();HoleWorkflow.Change(h,actor,g,r.Project!.ToleranceFor(h.Geometry),edit.Reason);changed.Add(h);
            }
            catch(InvalidOperationException error){skipped.Add((h.Mark.Length>0?h.Mark:h.Id)+": "+error.Message);}
        }
        if(changed.Count>0)controller.Save(r,changed,"Изменить размеры");
        SetStatus($"Размеры изменены у {changed.Count} из {holes.Count} отверстий."+(skipped.Count>0?Environment.NewLine+string.Join(Environment.NewLine,skipped.Take(5)):""));
    }
    void CopyLevels(Registry r,List<Hole> holes)
    {
        var window=new Window{Title="Копировать отверстия по уровням",Width=420,Height=420,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};BotPlusTheme.Apply(window);
        var panel=new DockPanel{Margin=new Thickness(14)};window.Content=panel;var list=new ListBox{ItemsSource=Levels,SelectionMode=SelectionMode.Multiple};var shaft=new CheckBox{Content="Создать логическую шахту"};var controls=new StackPanel();controls.Children.Add(shaft);controls.Children.Add(Button("Создать копии",()=>window.DialogResult=true));DockPanel.SetDock(controls,Dock.Bottom);panel.Children.Add(controls);panel.Children.Add(list);
        if(window.ShowDialog()!=true)return;var changed=new List<Hole>();
        foreach(var source in holes)
        {
            var members=new List<Hole>{source};foreach(var level in list.SelectedItems.OfType<LevelChoice>().Where(l=>l.Id!=source.LevelId))
            {var copy=HoleWorkflow.Copy(r.Project!,source,HoleStore.Actor(r),0,0,level.Elevation-source.LevelElevation,level.Id,level.Name,level.Elevation);copy.Mark=HoleController.NextMark(r,copy.Discipline);r.Holes.Add(copy);changed.Add(copy);members.Add(copy);}
            if(shaft.IsChecked==true){HoleChecks.SetShaft(members,HoleStore.Actor(r));changed.Add(source);}
        }
        controller.Save(r,changed,"Копировать по уровням");
    }
    void ImportLink(Registry registry)
    {
        HoleController.RequireProject(registry);if(links.SelectedItem is not LinkChoice chosen)throw new InvalidOperationException("Выберите связь с реестром отверстий.");
        var link=(Autodesk.Revit.DB.RevitLinkInstance)controller.Document.GetElement(chosen.Id);var source=HoleStore.Read(link.GetLinkDocument()??throw new InvalidOperationException("Связь не загружена."));HoleController.RequireProject(source);
        var transform=link.GetTotalTransform();var z=transform.OfVector(Autodesk.Revit.DB.XYZ.BasisZ);
        if(z.DistanceTo(Autodesk.Revit.DB.XYZ.BasisZ)>1e-7)throw new InvalidOperationException("Наклонённые связи пока не поддержаны. Используйте связь с вертикальной осью Z.");
        HoleGeometry Convert(HoleGeometry g)
        {var p=transform.OfPoint(HoleMarkers.Center(g));var u=transform.OfVector(HoleMarkers.U(g));return g with{X=HoleMarkers.Mm(p.X),Y=HoleMarkers.Mm(p.Y),Z=HoleMarkers.Mm(p.Z),Yaw=Math.Atan2(u.Y,u.X)};}
        Import(registry,HoleExchange.Export(source.Project!,source.Holes),Convert);
    }
    void Import(Registry registry,HolePackage package,Func<HoleGeometry,HoleGeometry>? transform=null)
    {
        HoleController.RequireProject(registry);if(HoleStore.Actor(registry).Role is not (Role.Architect or Role.Structural or Role.Admin))throw new InvalidOperationException("Задания в модель принимают АР, КР или администратор.");
        var before=registry.Holes.ToDictionary(h=>h.Key,h=>HoleJson.Hash(h));var result=HoleExchange.Import(registry.Project!,registry.Holes,package,approvedOnly.IsChecked==true,transform);registry.Holes=result.Holes;
        controller.Save(registry,registry.Holes.Where(h=>!before.TryGetValue(h.Key,out var hash)||hash!=HoleJson.Hash(h)),"Обновить задания");SetStatus(string.Join(Environment.NewLine,result.Changes.Take(15)));
    }
    void SaveSettings(Registry registry)
    {
        HoleController.RequireProject(registry);if(HoleStore.Actor(registry).Role!=Role.Admin)throw new InvalidOperationException("Настройки меняет администратор реестра.");
        var map=new Dictionary<string,Role>(StringComparer.OrdinalIgnoreCase);
        foreach(var line in users.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
        {
            var parts=line.Trim().TrimEnd(';').Split('=',2);if(parts.Length!=2||parts[0].Trim().Length==0)throw new InvalidOperationException("Формат: имя=роль;");
            string roleText=parts[1].Trim();var value=roleText switch{"Инженер"=>Role.Engineer,"Архитектор"=>Role.Architect,"Конструктор"=>Role.Structural,"Админ"=>Role.Admin,_=>Enum.TryParse<Role>(roleText,true,out var parsed)&&Enum.IsDefined(parsed)?parsed:throw new InvalidOperationException("Неизвестная роль: "+roleText)};
            if(!map.TryAdd(parts[0].Trim(),value))throw new InvalidOperationException("Пользователь указан дважды.");
        }
        if(!map.Values.Contains(Role.Admin))throw new InvalidOperationException("В реестре должен остаться администратор.");double number=Parse(tolerance.Text);var rule=new Tolerance(number,number,number,number);rule.Validate();
        registry.Project!.Users=map;foreach(var o in Enum.GetValues<HoleOrientation>())foreach(var s in Enum.GetValues<HoleShape>())registry.Project.Tolerances[o+"/"+s]=rule;
        registry.Project.AllowArchitectStructuralApproval=arkr.IsChecked==true;registry.Project.SettingsHistory.Add(DateTimeOffset.UtcNow+" "+HoleStore.User+" — обновлены роли и допуски: "+number+" мм.");
        HoleStore.InTransaction(controller.Document,"Отверстия — настройки",()=>HoleStore.Save(controller.Document,registry));SetStatus("Настройки сохранены в модели.");
    }
    static double Parse(string value)=>double.Parse(value.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture);
    string? Ask(string title,string value)
    {var box=new TextBox{Text=value,Margin=new Thickness(12)};var window=new Window{Title=title,Width=470,Height=145,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};BotPlusTheme.Apply(window);var panel=new DockPanel();window.Content=panel;var button=Button("Применить",()=>window.DialogResult=true);DockPanel.SetDock(button,Dock.Bottom);panel.Children.Add(button);panel.Children.Add(box);return window.ShowDialog()==true?box.Text:null;}
    void ExportCsv(DataGrid grid)
    {
        var dialog=new SaveFileDialog{Filter="Таблица CSV|*.csv",FileName="Проверка отверстий.csv"};if(dialog.ShowDialog(this)!=true)return;
        static string Cell(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
        var lines=new List<string>{"Первое;Второе;Уровень;Результат"};lines.AddRange(grid.Items.OfType<HoleIssue>().Select(i=>string.Join(";",new[]{i.First,i.Second,i.Level,i.Message}.Select(Cell))));File.WriteAllLines(dialog.FileName,lines,new System.Text.UTF8Encoding(true));SetStatus("Таблица CSV сохранена.");
    }
}


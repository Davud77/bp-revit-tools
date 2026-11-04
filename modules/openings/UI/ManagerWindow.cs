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
    readonly DataGrid table=MakeTable(),history=MakeTable(),checks=MakeTable(),crossings=MakeTable();
    readonly TextBox crossingGap=new(){Text="25",Width=65},crossingStep=new(){Text="10",Width=65},crossingSection=new(){Text="ОВ",Width=65};
    readonly CheckBox crossingInsulation=new(){Content="Изоляция",IsChecked=true},crossingLinks=new(){Content="Основы из связей",IsChecked=true},crossingSelected=new(){Content="Только выбранные MEP"};
    readonly ListBox crossingWarnings=new(){MaxHeight=130};
    CrossingOptions? scannedOptions;
    IReadOnlyCollection<Autodesk.Revit.DB.ElementId>? scannedSelection;
    readonly TextBlock heading=new(){FontSize=21,FontWeight=FontWeights.SemiBold},summary=new(),status=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBox search=new(){Width=220,Margin=new Thickness(8,0,8,0)},comment=new(){MinWidth=300};
    readonly ComboBox state=new(){Width=180,Margin=new Thickness(8,0,0,0)},role=new(){Width=90},links=new(){MinWidth=250};
    readonly CheckBox show3d=new(){Content="Показать в 3D",Margin=new Thickness(10,6,0,0)},showDeleted=new(){Content="Включая удалённые",Margin=new Thickness(10,6,0,0)},approvedOnly=new(){Content="Добавлять только полностью согласованные",IsChecked=true};
    readonly TextBox users=new(){AcceptsReturn=true,MinHeight=105,FontFamily=new FontFamily("Consolas"),VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    readonly TextBox tolerance=new(){Text="0"};
    readonly CheckBox arkr=new(){Content="Разрешить архитектору согласование за КР"};
    Registry data=new();
    readonly TabControl tabs=new();
    internal List<LevelChoice> Levels {get;private set;}=[];
    internal ManagerWindow(HoleController controller)
    {
        this.controller=controller;Title="BotPlus RT — Менеджер отверстий";Width=1250;Height=760;MinWidth=850;MinHeight=540;MaxHeight=SystemParameters.WorkArea.Height-24;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=new FontFamily("Segoe UI");FontSize=13;Background=new SolidColorBrush(Color.FromRgb(245,247,250));
        var root=new DockPanel{Margin=new Thickness(18)};Content=root;
        var header=new StackPanel{Margin=new Thickness(0,0,0,14)};header.Children.Add(heading);summary.Margin=new Thickness(0,6,0,0);header.Children.Add(summary);DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        status.Padding=new Thickness(8);status.Background=Brushes.White;status.Margin=new Thickness(0,10,0,0);DockPanel.SetDock(status,Dock.Bottom);root.Children.Add(status);
        root.Children.Add(tabs);
        var registry=new DockPanel();var filter=new WrapPanel{Margin=new Thickness(0,0,0,8)};
        filter.Children.Add(Button("Обновить",()=>controller.Request((_,_)=>SetStatus("Реестр обновлён."))));filter.Children.Add(search);
        state.ItemsSource=new[]{"Все статусы","Не согласовано","Частично согласовано","Согласовано","Отказ","На удаление","Изменено в модели"};state.SelectedIndex=0;filter.Children.Add(state);filter.Children.Add(showDeleted);filter.Children.Add(show3d);
        DockPanel.SetDock(filter,Dock.Top);registry.Children.Add(filter);
        var menus=new Menu{Margin=new Thickness(0,0,0,8),Background=Brushes.White};
        MenuItem Group(string title){var item=new MenuItem{Header=title};menus.Items.Add(item);return item;}
        void Action(MenuItem menu,string title,Action action){var item=new MenuItem{Header=title};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        var create=Group("Создать");Action(create,"По точке на стене / перекрытии…",()=>controller.Request((ui,r)=>controller.CreateManual(ui,r)));
        Action(create,"Копировать по точке",()=>WithSelected((ui,r,holes)=>controller.CopyPoint(ui,r,One(holes))));
        Action(create,"Копировать по уровням…",()=>WithSelected((_,r,holes)=>CopyLevels(r,holes)));
        var edit=Group("Изменить");Action(edit,"Положение и размеры…",()=>WithSelected((_,r,holes)=>controller.Edit(r,One(holes))));
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
        registry.Children.Add(table);AddTab("Реестр",registry);
        var crossingPanel=new DockPanel();var crossingControls=new WrapPanel{Margin=new Thickness(0,8,0,8)};
        void Option(string label,Control input){crossingControls.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,4,0)});input.Margin=new Thickness(0,3,8,3);crossingControls.Children.Add(input);}
        Option("Зазор с каждой стороны, мм",crossingGap);Option("Округление, мм",crossingStep);Option("Раздел",crossingSection);
        Option("",crossingInsulation);Option("",crossingLinks);Option("",crossingSelected);
        crossingControls.Children.Add(Button("Найти пересечения",()=>controller.Request((ui,r)=>
        {
            var options=ReadCrossingOptions();var selected=crossingSelected.IsChecked==true?ui.ActiveUIDocument.Selection.GetElementIds().ToArray():null;
            var found=HoleCrossings.Scan(controller.Document,r,options,selected);scannedOptions=options;scannedSelection=selected;
            crossings.ItemsSource=found.Candidates;crossingWarnings.ItemsSource=found.Warnings;
            SetStatus($"Коммуникаций: {found.Communications}; основ: {found.HostCount}; пересечений: {found.Candidates.Count}; новых заданий: {found.Candidates.Count(c=>c.CanCreate)}; замечаний: {found.Warnings.Count}. Выберите строки для создания. Поиск не изменяет модель.");
        })));
        crossingControls.Children.Add(Button("Создать выбранные задания",()=>
        {
            var chosen=crossings.SelectedItems.OfType<CrossingCandidate>().ToList();string discipline=crossingSection.Text.Trim();
            if(scannedOptions==null||scannedOptions!=ReadCrossingOptions())throw new InvalidOperationException("Сначала обновите поиск с текущими параметрами.");
            var options=scannedOptions;var selection=scannedSelection;
            controller.Request((_,r)=>{var created=HoleCrossings.Create(controller.Document,r,options,selection,chosen,discipline);controller.Save(r,created,"Создать задания по пересечениям");crossings.ItemsSource=null;scannedOptions=null;SetStatus($"Создано заданий: {created.Count}. Согласования и физические вырезы выполняются отдельно.");});
        }));
        DockPanel.SetDock(crossingControls,Dock.Top);crossingPanel.Children.Add(crossingControls);
        var crossingHelp=new TextBlock{Text="Прямые MEP текущего проекта → вертикальные прямые стены и горизонтальные перекрытия. Наклонный проход увеличивает прямоугольное задание. Поиск проверяет проход оси через толщу основы; касания краем профиля и фасонные части требуют отдельной проверки.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8,0,8,10)};
        DockPanel.SetDock(crossingHelp,Dock.Top);crossingPanel.Children.Add(crossingHelp);
        var warningPanel=new Expander{Header="Замечания поиска",Content=crossingWarnings,IsExpanded=true,Margin=new Thickness(0,8,0,0)};DockPanel.SetDock(warningPanel,Dock.Bottom);crossingPanel.Children.Add(warningPanel);
        AddColumns(crossings,("Состояние","State"),("Коммуникация","Communication"),("Основа","Host"),("Уровень","LevelName"),("Размер, мм","Size"),("Глубина, мм","Depth"),("X, мм","Geometry.X"),("Y, мм","Geometry.Y"),("Z, мм","Geometry.Z"));
        crossingPanel.Children.Add(crossings);AddTab("Пересечения MEP",crossingPanel);
        var historyPanel=new DockPanel();var commentRow=new WrapPanel{Margin=new Thickness(0,0,0,10)};commentRow.Children.Add(comment);commentRow.Children.Add(Button("Добавить комментарий",()=>Mutate("Комментарий",(r,h)=>HoleWorkflow.Comment(h,HoleStore.Actor(r),Comment()))));
        commentRow.Children.Add(Button("Архивировать прежнюю историю",()=>Mutate("Архивировать историю",(r,h)=>HoleWorkflow.ArchiveHistory(h,HoleStore.Actor(r)))));
        DockPanel.SetDock(commentRow,Dock.Top);historyPanel.Children.Add(commentRow);AddColumns(history,("Дата","TimeText"),("Пользователь","User"),("Роль","RoleText"),("Действие","Action"),("Комментарий","Comment"),("Ревизия","Revision"));historyPanel.Children.Add(history);AddTab("История",historyPanel);
        var checkPanel=new DockPanel();var checkButtons=new WrapPanel{Margin=new Thickness(0,0,0,10)};
        checkButtons.Children.Add(Button("Проверить расстояния…",()=>controller.Request((_,r)=>
        {var text=Ask("Минимальные расстояния X; Y; Z в мм","50;50;50");if(text==null)return;var v=text.Split(';').Select(Parse).ToArray();if(v.Length!=3)throw new InvalidOperationException("Введите три расстояния через точку с запятой.");checks.ItemsSource=HoleChecks.Spacing(r.Holes,new(v[0],v[1],v[2]));SetStatus("Проверка по габаритам завершена. Для повёрнутых отверстий уточните зазор в 3D.");})));
        checkButtons.Children.Add(Button("Показать пару",()=>{if(checks.SelectedItem is HoleIssue issue){var keys=new[]{issue.First,issue.Second};controller.Request((ui,r)=>controller.ShowHole(ui,r.Holes.Where(h=>keys.Contains(h.Key)).ToList(),true));}}));
        checkButtons.Children.Add(Button("Экспорт CSV",()=>ExportCsv(checks)));DockPanel.SetDock(checkButtons,Dock.Top);checkPanel.Children.Add(checkButtons);
        AddColumns(checks,("Первое отверстие","First"),("Второе отверстие","Second"),("Уровень","Level"),("Результат","Message"));checkPanel.Children.Add(checks);AddTab("Проверки",checkPanel);
        var exchange=new StackPanel{Margin=new Thickness(16)};exchange.Children.Add(new TextBlock{Text="Обмен заданиями",FontSize=19});exchange.Children.Add(new TextBlock{Text="Из связи используется её фактическое преобразование координат. JSON передаёт внутренние координаты в миллиметрах.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,14)});
        exchange.Children.Add(links);exchange.Children.Add(approvedOnly);exchange.Children.Add(Button("Обновить из выбранной связи",()=>controller.Request((_,r)=>ImportLink(r))));
        exchange.Children.Add(Button("Импорт JSON…",()=>{var dialog=new OpenFileDialog{Filter="Задания отверстий|*.json"};if(dialog.ShowDialog(this)==true){string file=dialog.FileName;controller.Request((_,r)=>Import(r,HoleJson.Read<HolePackage>(File.ReadAllText(file))));}}));
        exchange.Children.Add(Button("Экспорт исходного реестра JSON…",()=>{var dialog=new SaveFileDialog{Filter="Задания отверстий|*.json",FileName="Отверстия.json"};if(dialog.ShowDialog(this)==true){string file=dialog.FileName;controller.Request((_,r)=>{HoleController.RequireProject(r);File.WriteAllText(file,HoleJson.Write(HoleExchange.Export(r.Project!,r.Holes)));SetStatus("Задания экспортированы: "+file);});}}));AddTab("Обмен",exchange);
        var settings=new StackPanel{Margin=new Thickness(16)};settings.Children.Add(new TextBlock{Text="Настройки реестра",FontSize=19});settings.Children.Add(Button("Создать реестр и назначить себя первым администратором",()=>controller.Request((_,r)=>controller.Initialize(r))));
        settings.Children.Add(new TextBlock{Text="Пользователи: имя Windows=Инженер / Архитектор / Конструктор / Админ;",Margin=new Thickness(0,16,0,6)});settings.Children.Add(users);
        settings.Children.Add(Button("Прочитать список пользователей…",()=>{var dialog=new OpenFileDialog{Filter="Список пользователей|*.txt"};if(dialog.ShowDialog(this)==true)users.Text=File.ReadAllText(dialog.FileName);}));
        settings.Children.Add(new TextBlock{Text="Допуск перемещений и размеров (мм, для всех четырёх типов):",Margin=new Thickness(0,14,0,6)});settings.Children.Add(tolerance);settings.Children.Add(arkr);
        settings.Children.Add(Button("Сохранить настройки",()=>controller.Request((_,r)=>SaveSettings(r))));AddTab("Настройки",new ScrollViewer{Content=settings,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        search.TextChanged+=(_,_)=>Filter();state.SelectionChanged+=(_,_)=>Filter();showDeleted.Checked+=(_,_)=>Filter();showDeleted.Unchecked+=(_,_)=>Filter();
        table.SelectionChanged+=(_,_)=>UpdateHistory();table.MouseDoubleClick+=(_,_)=>{if(table.SelectedItem is Hole)WithSelected((ui,_,holes)=>controller.ShowHole(ui,holes,show3d.IsChecked==true));};
        SetStatus("Создайте или откройте реестр. Изменения сохраняются в RVT при обычном сохранении проекта.");
    }
    static DataGrid MakeTable()=>new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserSortColumns=true,SelectionMode=DataGridSelectionMode.Extended,SelectionUnit=DataGridSelectionUnit.FullRow,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,Background=Brushes.White,BorderBrush=Brushes.LightGray};
    static void AddColumns(DataGrid grid,params (string Header,string Path)[] columns)
    {
        foreach(var c in columns)
        {
            var binding=new Binding(c.Path);
            if(c.Path.StartsWith("Geometry.",StringComparison.Ordinal)||c.Path is "Bottom" or "Depth")binding.StringFormat="{0:0.#}";
            grid.Columns.Add(new DataGridTextColumn{Header=c.Header,Binding=binding,SortMemberPath=c.Path,MinWidth=70,Width=new DataGridLength(1,DataGridLengthUnitType.Auto)});
        }
    }
    static Button Button(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,0,8,5),HorizontalAlignment=HorizontalAlignment.Left};b.Click+=(_,_)=>{try{action();}catch(Exception error){MessageBox.Show(error.Message,"Менеджер отверстий");}};return b;}
    void AddTab(string title,object content)=>tabs.Items.Add(new TabItem{Header=title,Content=content,Padding=new Thickness(13,7,13,7)});
    internal void SetStatus(string text)=>status.Text=text;
    internal void SetData(Registry registry,List<LevelChoice> levels,List<LinkChoice> linked,string title)
    {
        data=registry;Levels=levels;heading.Text="Менеджер отверстий · "+title;var actor=HoleStore.Actor(data);
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
    CrossingOptions ReadCrossingOptions(){var value=new CrossingOptions(Parse(crossingGap.Text),Parse(crossingStep.Text),10,crossingInsulation.IsChecked==true,crossingLinks.IsChecked==true);value.Validate();return value;}
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
    void CopyLevels(Registry r,List<Hole> holes)
    {
        var window=new Window{Title="Копировать отверстия по уровням",Width=420,Height=420,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};
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
    {var box=new TextBox{Text=value,Margin=new Thickness(12)};var window=new Window{Title=title,Width=470,Height=145,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new DockPanel();window.Content=panel;var button=Button("Применить",()=>window.DialogResult=true);DockPanel.SetDock(button,Dock.Bottom);panel.Children.Add(button);panel.Children.Add(box);return window.ShowDialog()==true?box.Text:null;}
    void ExportCsv(DataGrid grid)
    {
        var dialog=new SaveFileDialog{Filter="Таблица CSV|*.csv",FileName="Проверка отверстий.csv"};if(dialog.ShowDialog(this)!=true)return;
        static string Cell(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
        var lines=new List<string>{"Первое;Второе;Уровень;Результат"};lines.AddRange(grid.Items.OfType<HoleIssue>().Select(i=>string.Join(";",new[]{i.First,i.Second,i.Level,i.Message}.Select(Cell))));File.WriteAllLines(dialog.FileName,lines,new System.Text.UTF8Encoding(true));SetStatus("Таблица CSV сохранена.");
    }
}


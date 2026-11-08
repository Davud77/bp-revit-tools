using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal sealed class HoleEditor:Window
{
    readonly Dictionary<string,TextBox> fields=[];
    readonly ComboBox shape=new(){ItemsSource=Enum.GetValues<HoleShape>()};
    readonly ComboBox levels=new();
    readonly CheckBox shaft=new(){Content="Применить смещение и размеры ко всей шахте"};
    readonly TextBox discipline=new(){Text="ОВ"};
    readonly TextBox comment=new(){MinHeight=54,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap};
    readonly HoleGeometry initial;
    internal HoleGeometry Geometry {get;private set;}
    internal LevelChoice Level=>(LevelChoice)levels.SelectedItem;
    internal string Discipline=>discipline.Text.Trim();
    internal string Comment=>comment.Text.Trim();
    internal bool ApplyShaft=>shaft.IsChecked==true;
    internal HoleEditor(HoleGeometry geometry,IReadOnlyList<LevelChoice> choices,string levelId,string section,bool hasShaft=false)
    {
        initial=Geometry=geometry;Title="Задание отверстия";Width=470;Height=690;MaxHeight=SystemParameters.WorkArea.Height-30;ResizeMode=ResizeMode.CanResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;BotPlusTheme.Apply(this);
        var root=new DockPanel{Margin=new Thickness(18)};Content=root;
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var save=new Button{Content="Сохранить задание",Padding=new Thickness(14,7,14,7),IsDefault=true};var cancel=new Button{Content="Отмена",Padding=new Thickness(14,7,14,7),Margin=new Thickness(8,0,0,0),IsCancel=true};
        actions.Children.Add(save);actions.Children.Add(cancel);DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        var panel=new StackPanel();root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        void Field(string title,Control control){panel.Children.Add(new TextBlock{Text=title,Margin=new Thickness(0,8,0,4)});panel.Children.Add(control);}
        var shapeLabels=new[]{new ShapeChoice(HoleShape.Rectangle,"Прямоугольное"),new ShapeChoice(HoleShape.Round,"Круглое")};shape.ItemsSource=shapeLabels;shape.SelectedItem=shapeLabels.First(s=>s.Shape==geometry.Shape);Field("Форма",shape);
        levels.ItemsSource=choices;levels.SelectedItem=choices.FirstOrDefault(l=>l.Id==levelId)??choices.FirstOrDefault();Field("Уровень",levels);discipline.Text=section;Field("Раздел",discipline);
        void Number(string key,string title,double value){var box=new TextBox{Text=value.ToString("0.###",CultureInfo.CurrentCulture)};fields.Add(key,box);Field(title,box);}
        Number("x","Центр X, мм (внутренние координаты)",geometry.X);Number("y","Центр Y, мм (внутренние координаты)",geometry.Y);Number("z","Центр / ось от уровня, мм",geometry.Z-(Level?.Elevation??0));
        Number("w","Ширина, мм",geometry.Width);Number("h","Высота / второй размер, мм",geometry.Height);Number("d","Диаметр, мм",geometry.Diameter);Number("depth","Глубина, мм",geometry.Depth);
        Number("yaw","Поворот в плане, градусы",geometry.Yaw*180/Math.PI);Field("Причина изменения / комментарий",comment);shaft.Visibility=hasShaft?Visibility.Visible:Visibility.Collapsed;shaft.Margin=new Thickness(0,10,0,0);panel.Children.Add(shaft);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)};error.SetResourceReference(TextBlock.ForegroundProperty,"BP.Error");panel.Children.Add(error);
        save.Click+=(_,_)=>
        {
            try
            {
                double Read(string key){string text=fields[key].Text.Trim().Replace(',','.');if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value))throw new InvalidOperationException("Проверьте числовые значения.");return value;}
                if(levels.SelectedItem is not LevelChoice chosen)throw new InvalidOperationException("Выберите уровень.");
                Geometry=initial with{Shape=((ShapeChoice)shape.SelectedItem).Shape,X=Read("x"),Y=Read("y"),Z=chosen.Elevation+Read("z"),Width=Read("w"),Height=Read("h"),Diameter=Read("d"),Depth=Read("depth"),Yaw=Read("yaw")*Math.PI/180};
                Geometry.Validate();if(Discipline.Length==0)throw new InvalidOperationException("Укажите раздел.");DialogResult=true;
            }catch(Exception ex){error.Text=ex.Message;}
        };
    }
    sealed record ShapeChoice(HoleShape Shape,string Name){public override string ToString()=>Name;}
}

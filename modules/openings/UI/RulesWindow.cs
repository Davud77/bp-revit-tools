using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using BotPlus.Openings.Core;
using Grid=System.Windows.Controls.Grid;
using Orientation=System.Windows.Controls.Orientation;

namespace BotPlus.Openings;

/// <summary>Правила создания отверстий: размеры ширины, высоты и диаметра относительно сети, округление, глубина, склейка.</summary>
internal sealed class RulesWindow:Window
{
    readonly TextBox width=Box(),height=Box(),diameter=Box(),step=Box(),depth=Box(),minimum=Box(),joinGap=Box();
    readonly CheckBox insulation=new(){Content="Учитывать изоляцию труб и воздуховодов"};
    readonly CheckBox roundForRound=new(){Content="Круглая сеть при прямом проходе — круглое отверстие (цилиндр)"};
    readonly CheckBox join=new(){Content="Склеивать примыкающие отверстия в одно"};
    CrossingOptions? result;
    static TextBox Box()=>new(){Width=90,Margin=new Thickness(0,3,8,3)};
    RulesWindow(CrossingOptions current)
    {
        Title="Правила создания отверстий";Width=640;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height-40;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
        BotPlusTheme.Apply(this);
        var panel=new StackPanel{Margin=new Thickness(16)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        TextBlock Heading(string text)=>new(){Text=text,FontSize=13,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,4)};
        TextBlock Hint(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Opacity=0.7,Margin=new Thickness(0,0,0,6)};
        UIElement Row(string label,TextBox input,string unit="мм")
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(new TextBlock{Text=label,Width=300,VerticalAlignment=VerticalAlignment.Center});
            row.Children.Add(input);row.Children.Add(new TextBlock{Text=unit,VerticalAlignment=VerticalAlignment.Center,Opacity=0.7});return row;
        }
        panel.Children.Add(Heading("Размер отверстия относительно сети"));
        panel.Children.Add(Hint("Запас добавляется с каждой стороны к размеру сети (с изоляцией, если она учитывается). Для наклонных проходов размер увеличивается автоматически."));
        panel.Children.Add(Row("Ширина: запас с каждой стороны",width));panel.Children.Add(Row("Высота: запас с каждой стороны",height));panel.Children.Add(Row("Диаметр: запас с каждой стороны",diameter));
        panel.Children.Add(Row("Округление размеров вверх до",step));panel.Children.Add(Row("Минимальный размер (ширина, высота, диаметр)",minimum));
        panel.Children.Add(Heading("Глубина"));
        panel.Children.Add(Row("Запас к толщине стены или перекрытия",depth));
        panel.Children.Add(Heading("Форма и состав"));
        foreach(var box in new CheckBox[]{insulation,roundForRound})box.Margin=new Thickness(0,3,0,3);panel.Children.Add(insulation);panel.Children.Add(roundForRound);
        panel.Children.Add(Heading("Склейка"));
        panel.Children.Add(Hint("При создании заданий отверстия одной стены или перекрытия, расстояние между которыми не больше указанного, заменяются одним прямоугольным отверстием по общему габариту. Цепочки склеиваются целиком."));
        join.Margin=new Thickness(0,3,0,3);panel.Children.Add(join);panel.Children.Add(Row("Расстояние склейки",joinGap));
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};
        Button Make(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(16,5,16,5),Margin=new Thickness(8,0,0,0),MinWidth=90};b.Click+=(_,_)=>action();return b;}
        buttons.Children.Add(Make("По умолчанию",()=>Load(new CrossingOptions())));
        buttons.Children.Add(Make("Применить",Accept));buttons.Children.Add(Make("Отмена",()=>DialogResult=false));panel.Children.Add(buttons);
        Load(current);
    }
    void Load(CrossingOptions o)
    {
        string F(double v)=>v.ToString("0.##",CultureInfo.CurrentCulture);
        width.Text=F(o.Clearance);height.Text=F(o.HeightClearance);diameter.Text=F(o.DiameterClearance);step.Text=F(o.Step);depth.Text=F(o.DepthMargin);minimum.Text=F(o.MinSize);joinGap.Text=F(o.MergeGap);
        insulation.IsChecked=o.IncludeInsulation;roundForRound.IsChecked=o.RoundForRound;join.IsChecked=o.MergeAdjacent;
    }
    static double Parse(TextBox box,string name)
    {
        if(double.TryParse(box.Text.Trim().Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var value))return value;
        throw new InvalidOperationException($"«{name}»: введите число.");
    }
    void Accept()
    {
        try
        {
            var options=new CrossingOptions(Parse(width,"Ширина"),Parse(step,"Округление"),Parse(depth,"Запас глубины"),insulation.IsChecked==true,true,
                Parse(height,"Высота"),Parse(diameter,"Диаметр"),Parse(minimum,"Минимальный размер"),roundForRound.IsChecked==true,join.IsChecked==true,Parse(joinGap,"Расстояние склейки"));
            options.Validate();result=options;DialogResult=true;
        }
        catch(Exception error){MessageBox.Show(this,error.Message,"Правила создания отверстий");}
    }
    /// <summary>Возвращает новые правила или null, если окно закрыто без применения.</summary>
    internal static CrossingOptions? Edit(Window owner,CrossingOptions current)
    {
        var window=new RulesWindow(current){Owner=owner,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        return window.ShowDialog()==true?window.result:null;
    }
}

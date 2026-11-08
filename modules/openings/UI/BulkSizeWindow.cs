using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Orientation=System.Windows.Controls.Orientation;

namespace BotPlus.Openings;

internal sealed record SizeEdit(double? Width,double? Height,double? Diameter,double? Depth,bool Add,string Reason);

/// <summary>Массовое изменение размеров выбранных отверстий: новое значение или прибавка к текущему.</summary>
internal sealed class BulkSizeWindow:Window
{
    readonly TextBox width=Box(),height=Box(),diameter=Box(),depth=Box(),reason=new(){Margin=new Thickness(0,3,0,3)};
    readonly RadioButton set=new(){Content="Задать значение",IsChecked=true,Margin=new Thickness(0,2,16,2)},add=new(){Content="Прибавить к текущему (можно со знаком минус)",Margin=new Thickness(0,2,0,2)};
    SizeEdit? result;
    static TextBox Box()=>new(){Width=90,Margin=new Thickness(0,3,8,3)};
    BulkSizeWindow(int count)
    {
        Title="Размеры выбранных отверстий";Width=560;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;BotPlusTheme.Apply(this);
        var panel=new StackPanel{Margin=new Thickness(16)};Content=panel;
        panel.Children.Add(new TextBlock{Text=$"Выбрано отверстий: {count}. Пустое поле — размер не меняется. Ширина и высота применяются к прямоугольным отверстиям, диаметр — к круглым. Положение центра не меняется.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        var mode=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,6)};mode.Children.Add(set);mode.Children.Add(add);panel.Children.Add(mode);
        void Row(string label,TextBox box){var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(new TextBlock{Text=label,Width=230,VerticalAlignment=VerticalAlignment.Center});row.Children.Add(box);row.Children.Add(new TextBlock{Text="мм",VerticalAlignment=VerticalAlignment.Center,Opacity=0.7});panel.Children.Add(row);}
        Row("Ширина",width);Row("Высота",height);Row("Диаметр",diameter);Row("Глубина",depth);
        panel.Children.Add(new TextBlock{Text="Причина (обязательна, если у отверстий есть согласования и размер выходит за допуск)",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,2)});panel.Children.Add(reason);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        Button Make(string text,Action action){var b=new Button{Content=text,Padding=new Thickness(16,5,16,5),Margin=new Thickness(8,0,0,0),MinWidth=90};b.Click+=(_,_)=>action();return b;}
        buttons.Children.Add(Make("Применить",Accept));buttons.Children.Add(Make("Отмена",()=>DialogResult=false));panel.Children.Add(buttons);
    }
    static double? Parse(TextBox box,string name)
    {
        string text=box.Text.Trim();if(text.Length==0)return null;
        if(double.TryParse(text.Replace(',','.'),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value))return value;
        throw new InvalidOperationException($"«{name}»: введите число или оставьте поле пустым.");
    }
    void Accept()
    {
        try
        {
            var edit=new SizeEdit(Parse(width,"Ширина"),Parse(height,"Высота"),Parse(diameter,"Диаметр"),Parse(depth,"Глубина"),add.IsChecked==true,reason.Text.Trim());
            if(edit.Width==null&&edit.Height==null&&edit.Diameter==null&&edit.Depth==null)throw new InvalidOperationException("Укажите хотя бы один размер.");
            result=edit;DialogResult=true;
        }
        catch(Exception error){MessageBox.Show(this,error.Message,"Размеры отверстий");}
    }
    internal static SizeEdit? Ask(Window owner,int count)
    {
        var window=new BulkSizeWindow(count){Owner=owner,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        return window.ShowDialog()==true?window.result:null;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace BotPlus.Openings;

internal sealed class LinkItem(string id,string name):INotifyPropertyChanged
{
    bool isChecked;string order="";
    public string Id {get;}=id;
    public string Name {get;}=name;
    public bool IsChecked {get=>isChecked;set{if(isChecked==value)return;isChecked=value;Raise(nameof(IsChecked));Toggled?.Invoke(this);}}
    public string OrderText {get=>order;set{if(order==value)return;order=value;Raise(nameof(OrderText));}}
    internal event Action<LinkItem>? Toggled;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name)=>PropertyChanged?.Invoke(this,new(name));
}

/// <summary>Выпадающий список связей с флажками; порядок отметки показывается номерами. Только интерфейс: поиск и создание его пока не читают.</summary>
internal sealed class LinkPicker:Grid
{
    readonly ToggleButton toggle=new(){MinWidth=260,HorizontalContentAlignment=HorizontalAlignment.Left};
    readonly Popup popup;
    readonly ObservableCollection<LinkItem> items=[];
    readonly List<LinkItem> order=[];
    readonly TextBlock empty=new(){Text="Нет загруженных связей",Margin=new Thickness(10,8,10,8),Opacity=0.7};
    readonly string placeholder;
    internal event Action? SelectionChanged;
    internal LinkPicker(string placeholder)
    {
        this.placeholder=placeholder;Margin=new Thickness(0,3,8,3);
        toggle.SetResourceReference(StyleProperty,"BP.PickerToggle");Children.Add(toggle);
        var list=new ItemsControl{ItemsSource=items};list.SetResourceReference(ItemsControl.ItemTemplateProperty,"BP.LinkRow");
        var all=new Button{Content="Выбрать все",Padding=new Thickness(8,3,8,3),Margin=new Thickness(6,6,0,6)};all.Click+=(_,_)=>SetAll(true);
        var none=new Button{Content="Снять выбор",Padding=new Thickness(8,3,8,3),Margin=new Thickness(6,6,6,6)};none.Click+=(_,_)=>SetAll(false);
        var bar=new StackPanel{Orientation=Orientation.Horizontal};bar.Children.Add(all);bar.Children.Add(none);
        var body=new StackPanel();body.Children.Add(bar);body.Children.Add(empty);body.Children.Add(list);
        var frame=new Border{BorderThickness=new Thickness(1),Child=new ScrollViewer{Content=body,MaxHeight=320,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},Margin=new Thickness(0,1,0,0)};
        frame.SetResourceReference(Border.BackgroundProperty,"BP.Input");frame.SetResourceReference(Border.BorderBrushProperty,"BP.Border");
        popup=new Popup{PlacementTarget=toggle,Placement=PlacementMode.Bottom,StaysOpen=false,Child=frame};
        popup.SetBinding(Popup.IsOpenProperty,new System.Windows.Data.Binding(nameof(ToggleButton.IsChecked)){Source=toggle,Mode=System.Windows.Data.BindingMode.TwoWay});
        popup.Opened+=(_,_)=>frame.MinWidth=toggle.ActualWidth;
        // Повторный щелчок по кнопке при открытом списке только закрывает его, а не открывает заново.
        popup.Opened+=(_,_)=>toggle.IsHitTestVisible=false;popup.Closed+=(_,_)=>toggle.IsHitTestVisible=true;
        Children.Add(popup);Refresh();
    }
    internal IReadOnlyList<string> SelectedIds=>order.Select(i=>i.Id).ToList();
    internal void SetItems(IEnumerable<LinkChoice> choices)
    {
        var keep=order.Select(i=>i.Id).ToList();order.Clear();items.Clear();
        foreach(var choice in choices)
        {
            var item=new LinkItem(choice.Id,choice.Name);item.Toggled+=OnToggled;items.Add(item);
        }
        foreach(var id in keep)if(items.FirstOrDefault(i=>i.Id==id) is {} found){order.Add(found);found.IsChecked=true;}
        Renumber();Refresh();
    }
    void OnToggled(LinkItem item)
    {
        if(item.IsChecked){if(!order.Contains(item))order.Add(item);}else order.Remove(item);
        Renumber();Refresh();SelectionChanged?.Invoke();
    }
    void SetAll(bool value){foreach(var item in items.ToList())if(item.IsChecked!=value)item.IsChecked=value;}
    void Renumber(){foreach(var item in items)item.OrderText=order.IndexOf(item) is var n&&n>=0?(n+1)+". ":"";}
    void Refresh()
    {
        empty.Visibility=items.Count==0?Visibility.Visible:Visibility.Collapsed;
        toggle.Content=order.Count==0?placeholder:"Выбрано связей: "+order.Count;
    }
}

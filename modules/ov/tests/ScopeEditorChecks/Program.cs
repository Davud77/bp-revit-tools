using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HeatLossViewer;
using HeatLossViewer.Export;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ru-RU");
        var data=new ProjectCalculationInputs {Building=new(){["heatGain"]=5},FloorOverrides=new(){["L1"]=new(){["indoor"]=18}},RoomOverrides=new(){["r1"]=new(){["heatGain"]=75}}};
        var editor=new ProjectCalculationEditor(data,[("r1","Этаж 1 · 101 · Кабинет","L1","Этаж 1"),("r2","Этаж 1 · 102 · Холл","L1","Этаж 1"),("r3","Этаж 2 · 201 · Группа","L2","Этаж 2")],(_,_)=>{},_=>null);
        var app=new Application();var window=new Window {Title="Проверка таблиц исходных данных ОВ",Width=1040,Height=680,Content=editor};
        int exit=0;
        window.ContentRendered+=(_,_)=>
        {
            try
            {
                editor.SelectedIndex=1;window.UpdateLayout();
                var grid=((DockPanel)((TabItem)editor.Items[1]).Content).Children.OfType<DataGrid>().Single();
                Check(grid.Items.Count==2 && grid.Columns.Count==5,"one row per floor and four editable parameters");
                var row=(ProjectCalculationEditor.ScopeRow)grid.Items[0];grid.SelectedItem=row;grid.CurrentCell=new DataGridCellInfo(row,grid.Columns[1]);grid.BeginEdit();window.UpdateLayout();
                ((TextBox)grid.Columns[1].GetCellContent(row)).Text="23,5";
                Check(editor.Collect().FloorOverrides["L1"]["indoor"]==23.5,"active cell commits Russian decimal through DataGrid binding");
                editor.SelectedIndex=2;window.UpdateLayout();
                var rooms=((DockPanel)((TabItem)editor.Items[2]).Content).Children.OfType<DataGrid>().Single();
                Check(rooms.Items.Count==3,"one row per room");
                var r=(ProjectCalculationEditor.ScopeRow)rooms.Items[0];r.Values["heatGain"]="";r.Values["outdoor"]="-22.5";
                var collected=editor.Collect();Check(!collected.RoomOverrides["r1"].ContainsKey("heatGain") && collected.RoomOverrides["r1"]["outdoor"]==-22.5,"empty inherits and invariant decimal remains valid");
                r.Values["indoor"]="invalid";bool rejected=false;try{editor.Collect();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid table input blocks save");r.Values["indoor"]="";
                Check(data.FloorOverrides["L1"]["indoor"]==18 && data.RoomOverrides["r1"]["heatGain"]==75,"draft editing leaves original model inputs unchanged");
                if(!args.Contains("--show"))window.Close();
            }
            catch(Exception ex){Console.Error.WriteLine(ex);exit=1;window.Close();}
        };
        app.Run(window);return exit;
    }
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
}
namespace HeatLossViewer.Export { public static class BuildingExportWriter { public static JsonSerializerOptions JsonOptions {get;}=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase}; } }

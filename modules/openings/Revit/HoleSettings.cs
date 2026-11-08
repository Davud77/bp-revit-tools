using System.Text.Json;
using BotPlus.Openings.Core;

namespace BotPlus.Openings;

internal enum ColouringMode {Discipline,Status}
/// <summary>Личные настройки пользователя; хранятся вне модели, чтобы не требовать прав администратора реестра.</summary>
internal static class HoleSettings
{
    sealed class Data
    {
        public string Colouring {get;set;}=nameof(ColouringMode.Discipline);
        public CrossingOptions? Rules {get;set;}
    }
    static string File=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BotPlus.RT","Openings","settings.json");
    static Data? cached;
    static Data Current
    {
        get
        {
            if(cached!=null)return cached;
            try{if(System.IO.File.Exists(File))cached=JsonSerializer.Deserialize<Data>(System.IO.File.ReadAllText(File));}catch{}
            return cached??=new Data();
        }
    }
    static void Save()
    {
        try{Directory.CreateDirectory(Path.GetDirectoryName(File)!);System.IO.File.WriteAllText(File,JsonSerializer.Serialize(Current));}catch{}
    }
    internal static ColouringMode Colouring
    {
        get=>Enum.TryParse<ColouringMode>(Current.Colouring,out var mode)?mode:ColouringMode.Discipline;
        set{Current.Colouring=value.ToString();Save();}
    }
    /// <summary>Правила расчёта размеров и склейки; при ошибке чтения — значения по умолчанию.</summary>
    internal static CrossingOptions Rules
    {
        get
        {
            var rules=Current.Rules??new CrossingOptions();
            try{rules.Validate();return rules with{IncludeLinks=true};}catch{return new CrossingOptions();}
        }
        set{value.Validate();Current.Rules=value;Save();}
    }
    /// <summary>Устойчивый цвет раздела: известные разделы фиксированы, остальные — по хешу названия.</summary>
    internal static (byte R,byte G,byte B) DisciplineColour(string discipline)
    {
        switch(discipline.Trim().ToUpperInvariant())
        {
            case "ОВ": return (40,120,220);
            case "ВК": return (30,170,160);
            case "ЭОМ": case "ЭЛ": return (235,150,30);
            case "СС": case "СС/АПС": return (150,90,200);
            case "ТХ": return (200,80,150);
        }
        int hash=0;foreach(char c in discipline)hash=hash*31+c;double hue=(Math.Abs(hash)%360)/60.0;
        double x=1-Math.Abs(hue%2-1);(double r,double g,double b)=(int)hue switch{0=>(1.0,x,0.0),1=>(x,1.0,0.0),2=>(0.0,1.0,x),3=>(0.0,x,1.0),4=>(x,0.0,1.0),_=>(1.0,0.0,x)};
        return ((byte)(60+r*160),(byte)(60+g*160),(byte)(60+b*160));
    }
}

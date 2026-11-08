using Autodesk.Revit.DB;

namespace BotPlus.Openings;

/// <summary>Равномерная сетка по XY для быстрого отбора габаритов вместо перебора всех пар.</summary>
internal sealed class BoxGrid<T> where T:notnull
{
    readonly double cell;readonly Dictionary<(int,int),List<T>> cells=[];
    internal BoxGrid(double cellFeet=33){cell=cellFeet;}
    (int,int,int,int) Range(XYZ min,XYZ max)=>((int)Math.Floor(min.X/cell),(int)Math.Floor(min.Y/cell),(int)Math.Floor(max.X/cell),(int)Math.Floor(max.Y/cell));
    internal void Add(T item,XYZ min,XYZ max)
    {
        var (x0,y0,x1,y1)=Range(min,max);
        // Очень большой габарит (длинная стена) занимает много ячеек; ограничиваем счёт ячеек, остальное — общий список.
        if((long)(x1-x0+1)*(y1-y0+1)>400){Wide.Add(item);return;}
        for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)
        {if(!cells.TryGetValue((x,y),out var list))cells[(x,y)]=list=[];list.Add(item);}
    }
    readonly List<T> Wide=[];
    internal IEnumerable<T> Query(XYZ min,XYZ max)
    {
        var seen=new HashSet<T>();var (x0,y0,x1,y1)=Range(min,max);
        foreach(var item in Wide)if(seen.Add(item))yield return item;
        if((long)(x1-x0+1)*(y1-y0+1)>400)
        {
            foreach(var list in cells.Values)foreach(var item in list)if(seen.Add(item))yield return item;
            yield break;
        }
        for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)
            if(cells.TryGetValue((x,y),out var list))foreach(var item in list)if(seen.Add(item))yield return item;
    }
}

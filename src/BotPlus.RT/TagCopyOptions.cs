namespace BotPlus.RT;

internal enum TagCopyScope { Family, Category, All }

internal static class TagCopyOptions
{
    internal static IReadOnlyList<string> Labels {get;}=Array.AsReadOnly(new[]{"То же семейство","Та же категория","Все"});
    static int selectedIndex;
    internal static int SelectedIndex
    {
        get=>selectedIndex;
        set=>selectedIndex=value>=0&&value<Labels.Count?value:0;
    }
    internal static TagCopyScope Current=>(TagCopyScope)selectedIndex;
    internal static void Reset()=>selectedIndex=0;
}

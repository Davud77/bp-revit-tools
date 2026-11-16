using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BotPlus.Eom.Core;

/// <summary>Прототип помещения: норма освещённости и условия среды для типового помещения (аналог «Прототипов комнат» nanoCAD).</summary>
public sealed class RoomPrototype
{
    /// <summary>Путь в дереве через «\»: норматив\раздел\подраздел.</summary>
    public string Group { get; set; } = "";
    /// <summary>Наименование с номером пункта нормы, например «60 Спальни».</summary>
    public string Name { get; set; } = "";
    public string VisualCategory { get; set; } = "-";
    public double Lux { get; set; } = 200;
    public double WorkPlaneM { get; set; } = 0.8;
    public double Maintenance { get; set; } = 1.4;
    public double Ceiling { get; set; } = 0.7;
    public double Walls { get; set; } = 0.5;
    public double Floor { get; set; } = 0.3;
    public string Environment { get; set; } = "Нормальная";
    public double Temperature { get; set; } = 20;
    /// <summary>Категория помещения по взрывопожарной и пожарной опасности (СП 12.13130).</summary>
    public string RoomCategory { get; set; } = "Не категорируется";
    /// <summary>Класс взрывоопасной зоны по ПУЭ.</summary>
    public string ExplosionZone { get; set; } = "Невзрывоопасная";
    /// <summary>Класс пожароопасной зоны по ПУЭ.</summary>
    public string FireZone { get; set; } = "Непожароопасная";
    /// <summary>Ключевые слова названия помещения через «;» для автоподбора прототипа.</summary>
    public string Keywords { get; set; } = "";

    /// <summary>Норматив — первый уровень дерева.</summary>
    [JsonIgnore] public string Norm => Segments.FirstOrDefault() ?? "";
    /// <summary>Номер раздела нормы («05» из «05. Дошкольные...»).</summary>
    [JsonIgnore] public string Section => Segments.Select(s => SectionPattern.Match(s)).LastOrDefault(m => m.Success)?.Groups[1].Value ?? "";

    /// <summary>Подпись для таблицы: «СП 52.13330.2016 05 60 Спальни». Для подпунктов без номера добавляется вышестоящий пункт.</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            var parts = new List<string> { Norm, Section };
            if (!char.IsDigit(Name.TrimStart().FirstOrDefault()))
            {
                // «а) ...» или «- ...»: перед ним — пункты после раздела («28 Помещения РУ», «а) внутри зданий»).
                int start = Segments.FindLastIndex(s => SectionPattern.IsMatch(s)) + 1;
                if (start > 0) parts.AddRange(Segments.Skip(start));
            }
            parts.Add(Name.Trim());
            return Regex.Replace(string.Join(" ", parts.Where(p => p.Length > 0)), @"\s+", " ");
        }
    }

    [JsonIgnore] List<string> Segments => Group.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    static readonly Regex SectionPattern = new(@"^(\d+)\.\s");
    public override string ToString() => Name;
}

/// <summary>
/// Библиотека прототипов помещений. Хранится в %APPDATA%\BotPlus RT\eom-rooms.json.
/// Если на компьютере установлен nanoCAD BIM Электро, при первом запуске из него импортируются все прототипы
/// (СП 52.13330.2016, СП 440.1325800.2018, СП 31-110-2003, ВСТП, ВНТП, МДС); иначе — встроенный сокращённый набор.
/// </summary>
public sealed class PrototypeLibrary
{
    public List<RoomPrototype> Items { get; set; } = [];
    /// <summary>Файл nanoCAD, из которого импортирована библиотека (пусто — встроенный набор).</summary>
    public string Source { get; set; } = "";

    public const string UserGroup = "Пользовательские";
    public static string DefaultPath => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "BotPlus RT", "eom-rooms.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Загружает библиотеку. Для файла по умолчанию без импорта пробует один раз импортировать прототипы nanoCAD.</summary>
    public static PrototypeLibrary Load(string? path = null)
    {
        bool standard = path == null;
        path ??= DefaultPath;
        PrototypeLibrary? lib = null;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<PrototypeLibrary>(File.ReadAllText(path)) is { } loaded && loaded.Items.Count > 0) lib = loaded;
        }
        catch { }
        lib ??= Builtin();
        if (standard && lib.Source.Length == 0 && NanoCadPrototypes.Find() is { } nano)
            try { lib.Import(nano); lib.Save(path); } catch { }
        return lib;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Options));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Заменяет библиотеку прототипами из файла nanoCAD; пользовательские прототипы сохраняются. Возвращает число импортированных.</summary>
    public int Import(string nanoCadFile)
    {
        var imported = NanoCadPrototypes.Read(nanoCadFile);
        if (imported.Count == 0) throw new InvalidOperationException("В файле нет прототипов помещений.");
        AutoMatch.Apply(imported);
        var user = Items.Where(i => i.Group == UserGroup).ToList();
        Items = [.. imported, .. user];
        if (!Items.Any(i => i.Group == UserGroup)) Items.Add(new() { Group = UserGroup, Name = "Прочие помещения", Lux = 200 });
        Source = nanoCadFile;
        return imported.Count;
    }

    /// <summary>Прототип по названию помещения: побеждает самое длинное совпавшее ключевое слово, при равенстве — первый в списке.</summary>
    public RoomPrototype? Match(string roomName)
    {
        string n = roomName.ToLowerInvariant().Replace('ё', 'е');
        RoomPrototype? best = null; int bestLength = 0;
        foreach (var p in Items)
            foreach (var key in p.Keywords.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string k = key.ToLowerInvariant().Replace('ё', 'е');
                if (k.Length > bestLength && n.Contains(k)) { best = p; bestLength = k.Length; }
            }
        return best;
    }

    /// <summary>
    /// Сокращённый встроенный набор по СП 52.13330.2016 — на случай, если nanoCAD не установлен. Значения — ориентир для
    /// типовых помещений: сверьте их с действующей редакцией свода правил или импортируйте полную библиотеку nanoCAD.
    /// </summary>
    public static PrototypeLibrary Builtin()
    {
        var lib = new PrototypeLibrary();
        const string Sp = "СП 52.13330.2016";
        void Add(string group, string name, double lux, string keys, double plane = 0.8, double kz = 1.4, double c = 0.7, double w = 0.5, double f = 0.3) =>
            lib.Items.Add(new() { Group = Sp + "\\" + group, Name = name, Lux = lux, Keywords = keys, WorkPlaneM = plane, Maintenance = kz, Ceiling = c, Walls = w, Floor = f });

        const string Office = "Общественные здания\\Административные здания, офисы";
        Add(Office, "Кабинеты, рабочие комнаты, офисы", 300, "кабинет;офис;рабочая комната;бухгалтер;приемная;приёмная");
        Add(Office, "Проектные залы, конструкторские и чертёжные бюро", 500, "проектн;конструктор;чертеж;чертёж");
        Add(Office, "Конференц-залы, залы заседаний", 200, "конференц;заседан;переговор");
        Add(Office, "Архивы, хранилища документов", 75, "архив", c: 0.5, w: 0.3, f: 0.1);

        const string Edu = "Общественные здания\\Учебные заведения";
        Add(Edu, "Учебные кабинеты, аудитории, лаборатории", 400, "класс;аудитор;учебн;лаборатор");
        Add(Edu, "Спортивные залы", 200, "спортзал;спортивный зал;спорт. зал", plane: 0);
        Add(Edu, "Читальные залы", 300, "читальн;библиот");

        const string Med = "Общественные здания\\Медицинские учреждения";
        Add(Med, "Кабинеты врачей", 500, "кабинет врач;врачебн;смотров;процедурн;манипуляц");
        Add(Med, "Палаты", 150, "палата", plane: 0);

        const string Trade = "Общественные здания\\Торговля и общественное питание";
        Add(Trade, "Торговые залы", 300, "торговый зал;торговый;магазин");
        Add(Trade, "Обеденные залы", 200, "обеденн;столов;кафе;ресторан;буфет");
        Add(Trade, "Горячие цеха, кухни предприятий питания", 200, "горячий цех;заготовочн;доготовоч;моечн");

        const string Aux = "Общественные здания\\Вспомогательные помещения";
        Add(Aux, "Вестибюли, гардеробные", 150, "вестибюл;гардероб;холл", plane: 0);
        Add(Aux, "Коридоры и проходы", 75, "коридор;проход;тамбур", plane: 0);
        Add(Aux, "Лестницы", 50, "лестн;л/к", plane: 0);
        Add(Aux, "Уборные, умывальные, душевые", 75, "с/у;санузел;сан. узел;уборн;туалет;умывальн;душ", plane: 0);
        Add(Aux, "Кладовые, хозяйственные помещения", 75, "кладов;хозяйств;инвентар;склад", plane: 0, c: 0.5, w: 0.3, f: 0.1);
        Add(Aux, "Электрощитовые, помещения распределительных устройств", 200, "электрощит;щитов;ру-0,4;эщ;вру", plane: 0.8, c: 0.5, w: 0.3, f: 0.1);
        Add(Aux, "Венткамеры, насосные, тепловые пункты", 50, "венткамер;вентиляцион;насосн;итп;тепловой пункт;водомер", plane: 0, c: 0.5, w: 0.3, f: 0.1);

        const string Home = "Жилые здания";
        Add(Home, "Жилые комнаты, гостиные, спальни", 150, "жилая;гостин;спальн;комната", plane: 0);
        Add(Home, "Кухни, кухни-ниши", 150, "кухня;кухни;кухня-гостин", plane: 0);
        Add(Home, "Детские комнаты", 200, "детская", plane: 0);
        Add(Home, "Кабинеты, библиотеки в квартирах", 300, "кабинет в кв");
        Add(Home, "Ванные, уборные, душевые в квартирах", 50, "ванн;с/у в кв;санузел в кв", plane: 0);
        Add(Home, "Коридоры, холлы в квартирах", 50, "прихож;холл в кв;коридор в кв", plane: 0);
        Add(Home, "Внеквартирные коридоры, лифтовые холлы", 20, "внеквартир;лифтов;поэтажн", plane: 0);

        lib.Items.Add(new() { Group = UserGroup, Name = "Прочие помещения", Lux = 200, Keywords = "" });
        return lib;
    }
}

/// <summary>Чтение базы «Прототипы комнат» nanoCAD BIM Электро (roomPrototypes.prt, XML).</summary>
public static class NanoCadPrototypes
{
    /// <summary>Файл прототипов самой новой установленной версии nanoCAD BIM Электро или null.</summary>
    public static string? Find()
    {
        try
        {
            string root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "Nanosoft");
            if (!Directory.Exists(root)) return null;
            return Directory.GetDirectories(root)
                .Select(d => Path.Combine(d, "RoomPrototypes", "roomPrototypes.prt")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch { return null; }
    }

    public static List<RoomPrototype> Read(string path)
    {
        var doc = XDocument.Load(path);
        var result = new List<RoomPrototype>();
        foreach (var r in doc.Root?.Elements("ElRoomPrototypeRecord") ?? [])
        {
            string S(string name) => (string?)r.Element(name) ?? "";
            double D(string name, double fallback) =>
                double.TryParse(S(name).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            string name = S("PrototypeName").Trim(), group = S("Category").Trim().TrimEnd('\\');
            if (name.Length == 0) continue;
            result.Add(new()
            {
                Group = group, Name = name, VisualCategory = S("EnvLightLookCategory").Trim() is { Length: > 0 } v ? v : "-",
                Lux = D("EnvLumLl", 0), WorkPlaneM = D("WorkSurfaceHeight", 0.8), Maintenance = D("SafeCoefK", 1.4),
                Ceiling = D("CeilReflection", 0.7), Walls = D("WallReflection", 0.5), Floor = D("FloorReflection", 0.3),
                Temperature = D("EnvTemperature", 20), Environment = Surrounding(S("EnvSurrounding")),
                RoomCategory = RoomCategory(S("EnvRoomCategory")), ExplosionZone = Explosion(S("EnvExplCategory")), FireZone = Fire(S("EnvFireCategory"))
            });
        }
        return result;
    }

    static string Surrounding(string v) => v switch
    {
        "Normal" => "Нормальная", "Moist" => "Влажная", "Raw" => "Сырая", "ExRaw" => "Особо сырая", "Dusty" => "Пыльная",
        "Hot" => "Жаркая", "ChemicalActivity" => "С химически активной средой", "" => "Нормальная", _ => v
    };
    static string RoomCategory(string v) => v switch
    {
        "Room_A" => "А", "Room_B" => "Б", "Room_B1" => "В1", "Room_B2" => "В2", "Room_B3" => "В3", "Room_B4" => "В4",
        "Room_G" => "Г", "Room_D" => "Д", _ => "Не категорируется"
    };
    static string Explosion(string v) => v switch
    {
        "Expl_B1" => "В-I", "Expl_B1a" => "В-Iа", "Expl_B1b" => "В-Iб", "Expl_B1g" => "В-Iг", "Expl_B2" => "В-II", "Expl_B2a" => "В-IIа", _ => "Невзрывоопасная"
    };
    static string Fire(string v) => v switch { "Fire_P1" => "П-I", "Fire_P2" => "П-II", "Fire_P2a" => "П-IIа", "Fire_P3" => "П-III", _ => "Непожароопасная" };
}

/// <summary>
/// Ключевые слова названий помещений для автоподбора прототипа СП 52.13330.2016 после импорта:
/// ветвь таблицы, номер раздела и начало наименования пункта.
/// </summary>
public static class AutoMatch
{
    const string Public = "Основные помещения общественных, жилых и вспомогательных зданий", Industrial = "Помещения и сооружения объектов общепромышленного назначения";

    public static readonly (string Branch, string Section, string Item, string Keywords)[] Rules =
    [
        (Public, "01", "1 ", "кабинет;офис;рабочая комната;рабочее место;бухгалтер;администрац"),
        (Public, "01", "2 ", "проектн;конструктор;чертеж"),
        (Public, "01", "3 ", "приемная;посетител;экспедиц"),
        (Public, "01", "4 ", "читальн"),
        (Public, "01", "6 ", "архив;книгохранил"),
        (Public, "01", "10 ", "компьютерный зал"),
        (Public, "01", "11 ", "конференц;заседан;переговор"),
        (Public, "01", "12 ", "рекреац;кулуар;фойе"),
        (Public, "02", "22 ", "серверн"),
        (Public, "03", "29 ", "класс;учебный кабинет"),
        (Public, "03", "30 ", "аудитор;лекцион"),
        (Public, "03", "37 ", "спортзал;спортивный зал;спорт. зал"),
        (Public, "03", "40 ", "актовый зал;киноаудитор"),
        (Public, "05", "56 ", "раздевальн"),
        (Public, "05", "57 ", "группов;игровая"),
        (Public, "08", "72 ", "обеденн;столов;кафе;ресторан;буфет"),
        (Public, "08", "74 ", "горячий цех;холодный цех;заготовочн"),
        (Public, "08", "76 ", "моечн"),
        (Public, "08", "81 ", "кладов;загрузочн;инвентар;хозяйствен"),
        (Public, "09", "83 ", "торговый зал;магазин"),
        (Public, "11", "105 ", "номер;гостиничн"),
        (Public, "12", "107 ", "палата"),
        (Public, "12", "112 ", "процедурн;смотров;манипуляц"),
        (Public, "12", "126 ", "кабинет врач;врачебн;прием врача"),
        (Public, "13", "184 ", "зал ожидания;ожидальн"),
        (Public, "14", "190 ", "жилая комната;жилая;комната;гостиная;спальн;детская"),
        (Public, "14", "191 ", "кухня;кухни;кухня-ниша;кухня-столовая;кухня-гостиная"),
        (Public, "14", "192 ", "прихож;ванн;гардеробная;коридор кв;холл кв;постирочн"),
        (Public, "14", "193 а)", "консьерж"),
        (Public, "15", "194 а)", "санузел;сан. узел;с/у;уборн;туалет;умывальн;пуи;курительн"),
        (Public, "15", "194 б)", "душев;раздевал"),
        (Public, "16", "196 б)", "вестибюл;холл;тамбур"),
        (Public, "16", "197 в)", "лестн;л/к"),
        (Public, "16", "198 а)", "лифтов"),
        (Public, "16", "199 а)", "коридор;проход;галерея"),
        (Public, "16", "200 ", "машинное помещение;машинное отделение;лифтерная"),
        (Public, "16", "201 ", "чердак"),
        (Industrial, "04", "33 ", "кабельн"),
        (Industrial, "04", "36 ", "электрощит;щитов;эщ;вру;гршн;грщ"),
        (Industrial, "05", "37 б)", "насосн"),
        (Industrial, "05", "45 ", "итп;тепловой пункт;теплов;кондиционер"),
        (Industrial, "05", "47 а)", "венткамер;вентиляцион;вент. камер"),
        (Industrial, "06", "64 ", "склад"),
        (Industrial, "09", "82 а)", "котельн;топочн"),
    ];

    /// <summary>Записывает ключевые слова правил в найденные прототипы. Возвращает правила, для которых пункт не найден.</summary>
    public static List<string> Apply(List<RoomPrototype> items)
    {
        var missing = new List<string>();
        foreach (var (branch, section, item, keys) in Rules)
        {
            var p = items.FirstOrDefault(i => i.Norm.StartsWith("СП 52.13330") && i.Group.Contains(branch) && i.Section == section && i.Name.StartsWith(item));
            if (p == null) { missing.Add($"{section} {item}"); continue; }
            p.Keywords = p.Keywords.Length == 0 ? keys : p.Keywords + ";" + keys;
        }
        return missing;
    }
}

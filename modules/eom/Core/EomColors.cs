namespace BotPlus.Eom.Core;

/// <summary>Цвета индикаторов статуса: одни и те же на плане (DirectContext3D) и на схеме ЭТМ.</summary>
public static class EomColors
{
    public static readonly (byte R, byte G, byte B) Connected = (40, 170, 70), Unconnected = (220, 40, 40), NoGroup = (150, 150, 150);
    static readonly (byte, byte, byte)[] Groups = [(40, 170, 70), (220, 40, 40), (40, 90, 220), (240, 170, 0), (160, 60, 200), (0, 170, 190), (230, 100, 160), (120, 120, 40)];
    /// <summary>Гр.1 — зелёный, Гр.2 — красный, Гр.3 — синий, далее по кругу. Индекс — порядок группы в сети (Network.AllCircuits).</summary>
    public static (byte R, byte G, byte B) Group(int index) => Groups[index % Groups.Length];
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using SystemColors = System.Windows.SystemColors;

// Файл подключается ссылкой в каждый проект с окнами (BotPlus.RT, Ac2Revit, ОВ, Отверстия),
// поэтому все типы internal: у каждой сборки своя копия, а настройки общие через файл.
namespace BotPlus.Shared;

/// <summary>Общие настройки плагина: тема окон и скрытые панели ленты. Хранятся в %APPDATA%\BotPlus RT\settings.json.</summary>
internal static class BotPlusSettings
{
    internal const string Follow = "follow", Light = "light", Dark = "dark";
    /// <summary>Панели вкладки «BotPlus RT» в порядке следования. Настройки скрыть нельзя.</summary>
    internal static readonly string[] Panels = ["MCP", "Ac2Revit", "ОВ", "Отверстия", "Марки"];
    internal static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BotPlus RT", "settings.json");

    internal static string ThemeMode { get; private set; } = Follow;
    internal static HashSet<string> HiddenPanels { get; private set; } = [];

    internal static void Load(string? path = null)
    {
        ThemeMode = Follow; HiddenPanels = [];
        try
        {
            path ??= FilePath;
            if (!File.Exists(path)) return;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (root.TryGetProperty("theme", out var theme) && theme.GetString() is Light or Dark) ThemeMode = theme.GetString()!;
            if (root.TryGetProperty("hiddenPanels", out var hidden) && hidden.ValueKind == JsonValueKind.Array)
                foreach (var item in hidden.EnumerateArray())
                    if (item.GetString() is { } name && Panels.Contains(name)) HiddenPanels.Add(name);
        }
        catch { ThemeMode = Follow; HiddenPanels = []; }
    }

    internal static void Save(string themeMode, IEnumerable<string> hiddenPanels, string? path = null)
    {
        path ??= FilePath;
        ThemeMode = themeMode is Light or Dark ? themeMode : Follow;
        HiddenPanels = hiddenPanels.Where(Panels.Contains).ToHashSet();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { theme = ThemeMode, hiddenPanels = Panels.Where(HiddenPanels.Contains).ToArray() }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Палитра и стили окон в духе диалогов Revit 2026. Тема берётся из настроек; по умолчанию следует теме Revit.</summary>
internal static class BotPlusTheme
{
    internal static bool? Override { get; set; }

    internal static bool DetectDark()
    {
        if (Override is { } forced) return forced;
        BotPlusSettings.Load();
        return BotPlusSettings.ThemeMode switch
        {
            BotPlusSettings.Dark => true,
            BotPlusSettings.Light => false,
            _ => RevitIsDark()
        };
    }

    internal static bool RevitIsDark()
    {
        try { return RevitDark(); }
        catch { return false; }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool RevitDark() => Autodesk.Revit.UI.UIThemeManager.CurrentTheme == Autodesk.Revit.UI.UITheme.Dark;

    static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    /// <summary>Применяет тему к окну или панели. Окно получает ещё и тёмную рамку заголовка.</summary>
    internal static void Apply(FrameworkElement element, bool? dark = null)
    {
        bool isDark = dark ?? DetectDark();
        string[] p = isDark
            ? ["#2F2F2F", "#3A3A3A", "#262626", "#5C5C5C", "#ECECEC", "#A6A6A6", "#0696D7", "#484848", "#0B5C86", "#434343"]
            : ["#F0F0F0", "#FAFAFA", "#FFFFFF", "#ABABAB", "#1E1E1E", "#6B6B6B", "#0696D7", "#DCEBF5", "#BFE0F4", "#E6E6E6"];
        var r = element.Resources;
        r["BP.Bg"] = Brush(p[0]); r["BP.Panel"] = Brush(p[1]); r["BP.Input"] = Brush(p[2]); r["BP.Border"] = Brush(p[3]); r["BP.Text"] = Brush(p[4]); r["BP.Muted"] = Brush(p[5]);
        r["BP.Done"] = Brush(isDark ? "#1F4A31" : "#CDEBD6"); r["BP.Accent"] = Brush(p[6]); r["BP.Hover"] = Brush(p[7]); r["BP.Selected"] = Brush(p[8]); r["BP.Button"] = Brush(p[9]);
        r["BP.Error"] = Brush(isDark ? "#FF8A80" : "#B3261E"); r["BP.Scroll"] = Brush(isDark ? "#6A6A6A" : "#B8B8B8"); r["BP.ScrollHot"] = Brush(isDark ? "#8A8A8A" : "#8F8F8F");
        // Системные кисти используют заводские шаблоны (поля ввода, выделение строк, меню).
        r[SystemColors.WindowBrushKey] = Brush(p[2]); r[SystemColors.WindowTextBrushKey] = Brush(p[4]);
        r[SystemColors.ControlBrushKey] = Brush(p[1]); r[SystemColors.ControlTextBrushKey] = Brush(p[4]);
        r[SystemColors.ControlLightBrushKey] = Brush(p[7]); r[SystemColors.ControlLightLightBrushKey] = Brush(p[1]); r[SystemColors.ControlDarkBrushKey] = Brush(p[3]);
        r[SystemColors.HighlightBrushKey] = Brush(p[8]); r[SystemColors.HighlightTextBrushKey] = Brush(p[4]);
        r[SystemColors.InactiveSelectionHighlightBrushKey] = Brush(p[8]); r[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brush(p[4]);
        r[SystemColors.MenuBrushKey] = Brush(p[1]); r[SystemColors.MenuBarBrushKey] = Brush(p[1]); r[SystemColors.MenuTextBrushKey] = Brush(p[4]);
        r[SystemColors.GrayTextBrushKey] = Brush(p[5]);
        r.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles));
        if (element is Window window)
        {
            window.FontFamily = new FontFamily("Segoe UI"); window.FontSize = 12;
            window.Background = (Brush)r["BP.Bg"]; window.Foreground = (Brush)r["BP.Text"];
            window.UseLayoutRounding = true; window.SnapsToDevicePixels = true;
            void Frame()
            {
                try
                {
                    int value = isDark ? 1 : 0; var handle = new WindowInteropHelper(window).Handle;
                    if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
                }
                catch { }
            }
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Frame(); else window.SourceInitialized += (_, _) => Frame();
        }
        else if (element is System.Windows.Controls.Control control)
        {
            control.Background = (Brush)r["BP.Bg"]; control.Foreground = (Brush)r["BP.Text"];
        }
        else if (element is System.Windows.Controls.Panel panel) panel.Background = (Brush)r["BP.Bg"];
    }

    /// <summary>Привязывает цвет элемента к ресурсу темы, чтобы он менялся вместе с темой.</summary>
    internal static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    { element.SetResourceReference(property, key); return element; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const string Styles = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="Background" Value="{DynamicResource BP.Button}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/>
    <Setter Property="MinHeight" Value="23"/><Setter Property="Padding" Value="12,2"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
      <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="2" Padding="{TemplateBinding Padding}">
        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsDefault" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Hover}"/><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Selected}"/></Trigger>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource BP.Muted}"/><Setter TargetName="b" Property="Opacity" Value="0.6"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="TabControl">
    <Setter Property="Background" Value="{DynamicResource BP.Bg}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl">
      <Grid><Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
        <TabPanel Grid.Row="0" IsItemsHost="True" Background="Transparent"/>
        <Border Grid.Row="1" BorderThickness="0,1,0,0" BorderBrush="{DynamicResource BP.Border}"><ContentPresenter ContentSource="SelectedContent" Margin="0,8,0,0"/></Border>
      </Grid></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="TabItem">
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
      <Border x:Name="b" Background="Transparent" BorderThickness="0,0,0,2" BorderBrush="Transparent" Padding="14,6" Margin="0,0,2,-1">
        <ContentPresenter x:Name="c" ContentSource="Header" TextElement.Foreground="{DynamicResource BP.Muted}"/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Hover}"/><Setter TargetName="c" Property="TextElement.Foreground" Value="{DynamicResource BP.Text}"/></Trigger>
        <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/><Setter TargetName="c" Property="TextElement.Foreground" Value="{DynamicResource BP.Text}"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="TextBox">
    <Setter Property="Background" Value="{DynamicResource BP.Input}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/><Setter Property="CaretBrush" Value="{DynamicResource BP.Text}"/>
    <Setter Property="SelectionBrush" Value="{DynamicResource BP.Accent}"/><Setter Property="Padding" Value="4,2"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
      <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="2">
        <ScrollViewer x:Name="PART_ContentHost" Padding="{TemplateBinding Padding}" Focusable="False"/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource BP.Muted}"/><Setter TargetName="b" Property="Opacity" Value="0.7"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="TextBlock"><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/></Style>
  <Style TargetType="Label"><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/></Style>
  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
      <BulletDecorator Background="Transparent">
        <BulletDecorator.Bullet>
          <Border x:Name="box" Width="14" Height="14" Background="{DynamicResource BP.Input}" BorderBrush="{DynamicResource BP.Border}" BorderThickness="1" CornerRadius="2">
            <Path x:Name="mark" Data="M3,7 L6,10 L11,3" Stroke="White" StrokeThickness="1.8" Visibility="Collapsed" Stretch="None"/></Border>
        </BulletDecorator.Bullet>
        <ContentPresenter Margin="6,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
      </BulletDecorator>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsChecked" Value="True"><Setter TargetName="mark" Property="Visibility" Value="Visible"/><Setter TargetName="box" Property="Background" Value="{DynamicResource BP.Accent}"/><Setter TargetName="box" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.55"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="RadioButton">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="VerticalContentAlignment" Value="Center"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
      <BulletDecorator Background="Transparent">
        <BulletDecorator.Bullet>
          <Grid Width="14" Height="14"><Ellipse x:Name="ring" Fill="{DynamicResource BP.Input}" Stroke="{DynamicResource BP.Border}" StrokeThickness="1"/>
            <Ellipse x:Name="dot" Fill="{DynamicResource BP.Accent}" Width="6" Height="6" Visibility="Collapsed"/></Grid>
        </BulletDecorator.Bullet>
        <ContentPresenter Margin="6,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True"/>
      </BulletDecorator>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ring" Property="Stroke" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsChecked" Value="True"><Setter TargetName="dot" Property="Visibility" Value="Visible"/><Setter TargetName="ring" Property="Stroke" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.55"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="GroupBox"><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/></Style>
  <Style TargetType="ProgressBar">
    <Setter Property="Foreground" Value="{DynamicResource BP.Accent}"/><Setter Property="Background" Value="{DynamicResource BP.Button}"/><Setter Property="Height" Value="6"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ProgressBar">
      <Grid><Border x:Name="PART_Track" Background="{TemplateBinding Background}" BorderBrush="{DynamicResource BP.Border}" BorderThickness="1"/>
        <Border x:Name="PART_Indicator" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}"/></Grid>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ToolTip"><Setter Property="Background" Value="{DynamicResource BP.Panel}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/></Style>
  <Style TargetType="ListBox"><Setter Property="Background" Value="{DynamicResource BP.Input}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/></Style>
  <Style TargetType="ListView"><Setter Property="Background" Value="{DynamicResource BP.Input}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/></Style>
  <Style TargetType="Expander"><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/></Style>
  <Style TargetType="Menu"><Setter Property="Background" Value="{DynamicResource BP.Panel}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/></Style>
  <ControlTemplate x:Key="BP.MenuTop" TargetType="MenuItem">
    <Border x:Name="b" Background="Transparent" BorderThickness="0,0,0,2" BorderBrush="Transparent" Padding="10,5">
      <Grid><ContentPresenter ContentSource="Header" RecognizesAccessKey="True" TextElement.Foreground="{DynamicResource BP.Text}"/>
        <Popup IsOpen="{Binding IsSubmenuOpen,RelativeSource={RelativeSource TemplatedParent}}" Placement="Bottom" Focusable="False" AllowsTransparency="True" PopupAnimation="None">
          <Border MinWidth="190" Background="{DynamicResource BP.Panel}" BorderBrush="{DynamicResource BP.Border}" BorderThickness="1" Padding="0,3">
            <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/></Border></Popup></Grid></Border>
    <ControlTemplate.Triggers>
      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Hover}"/></Trigger>
      <Trigger Property="IsSubmenuOpen" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <ControlTemplate x:Key="BP.MenuLeaf" TargetType="MenuItem">
    <Border x:Name="b" Background="Transparent" Padding="14,5">
      <ContentPresenter x:Name="c" ContentSource="Header" RecognizesAccessKey="True" TextElement.Foreground="{DynamicResource BP.Text}"/></Border>
    <ControlTemplate.Triggers>
      <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Selected}"/></Trigger>
      <Trigger Property="IsEnabled" Value="False"><Setter TargetName="c" Property="TextElement.Foreground" Value="{DynamicResource BP.Muted}"/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate>
  <Style TargetType="MenuItem">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="Template" Value="{StaticResource BP.MenuLeaf}"/>
    <Style.Triggers>
      <Trigger Property="Role" Value="TopLevelHeader"><Setter Property="Template" Value="{StaticResource BP.MenuTop}"/></Trigger>
      <Trigger Property="Role" Value="TopLevelItem"><Setter Property="Template" Value="{StaticResource BP.MenuTop}"/></Trigger>
    </Style.Triggers>
  </Style>
  <Style TargetType="ScrollViewer"><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/></Style>
  <Style TargetType="GridSplitter"><Setter Property="Background" Value="{DynamicResource BP.Border}"/></Style>
  <Style x:Key="BP.Page" TargetType="RepeatButton"><Setter Property="Focusable" Value="False"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter></Style>
  <Style x:Key="BP.ThumbStyle" TargetType="Thumb"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
    <Border x:Name="t" Background="{DynamicResource BP.Scroll}" Margin="2" CornerRadius="2"/>
    <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="t" Property="Background" Value="{DynamicResource BP.ScrollHot}"/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter></Style>
  <ControlTemplate x:Key="BP.ScrollV" TargetType="ScrollBar">
    <Grid Background="{DynamicResource BP.Bg}"><Track x:Name="PART_Track" IsDirectionReversed="True">
      <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Style="{StaticResource BP.Page}"/></Track.DecreaseRepeatButton>
      <Track.Thumb><Thumb Style="{StaticResource BP.ThumbStyle}"/></Track.Thumb>
      <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Style="{StaticResource BP.Page}"/></Track.IncreaseRepeatButton>
    </Track></Grid>
  </ControlTemplate>
  <ControlTemplate x:Key="BP.ScrollH" TargetType="ScrollBar">
    <Grid Background="{DynamicResource BP.Bg}"><Track x:Name="PART_Track">
      <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageLeftCommand" Style="{StaticResource BP.Page}"/></Track.DecreaseRepeatButton>
      <Track.Thumb><Thumb Style="{StaticResource BP.ThumbStyle}"/></Track.Thumb>
      <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageRightCommand" Style="{StaticResource BP.Page}"/></Track.IncreaseRepeatButton>
    </Track></Grid>
  </ControlTemplate>
  <Style TargetType="ScrollBar">
    <Setter Property="Width" Value="12"/><Setter Property="MinWidth" Value="12"/><Setter Property="Template" Value="{StaticResource BP.ScrollV}"/>
    <Style.Triggers><Trigger Property="Orientation" Value="Horizontal">
      <Setter Property="Width" Value="Auto"/><Setter Property="MinWidth" Value="0"/><Setter Property="Height" Value="12"/><Setter Property="MinHeight" Value="12"/><Setter Property="Template" Value="{StaticResource BP.ScrollH}"/>
    </Trigger></Style.Triggers>
  </Style>
  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
      <Border x:Name="b" Background="Transparent" Padding="8,4"><ContentPresenter/></Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Hover}"/></Trigger>
        <Trigger Property="IsSelected" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource BP.Selected}"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/><Setter Property="Background" Value="{DynamicResource BP.Input}"/><Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/>
    <Setter Property="MinHeight" Value="23"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
      <Grid>
        <ToggleButton x:Name="tb" Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}">
          <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
            <Border x:Name="bb" Background="{DynamicResource BP.Input}" BorderBrush="{DynamicResource BP.Border}" BorderThickness="1" CornerRadius="2">
              <Path HorizontalAlignment="Right" Margin="0,0,8,0" VerticalAlignment="Center" Data="M0,0 L4,4 L8,0" Stroke="{DynamicResource BP.Text}" StrokeThickness="1.4"/></Border>
            <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="bb" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></ToggleButton.Template></ToggleButton>
        <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" Margin="8,3,24,3" IsHitTestVisible="False" VerticalAlignment="Center" HorizontalAlignment="Left"/>
        <Popup IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" Focusable="False" AllowsTransparency="True" PopupAnimation="None">
          <Border MinWidth="{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="300" Background="{DynamicResource BP.Input}" BorderBrush="{DynamicResource BP.Border}" BorderThickness="1">
            <ScrollViewer><ItemsPresenter/></ScrollViewer></Border></Popup>
      </Grid></ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style TargetType="DataGrid">
    <Setter Property="Background" Value="{DynamicResource BP.Input}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/><Setter Property="RowBackground" Value="{DynamicResource BP.Input}"/>
    <Setter Property="HorizontalGridLinesBrush" Value="{DynamicResource BP.Border}"/><Setter Property="VerticalGridLinesBrush" Value="{DynamicResource BP.Border}"/>
    <Setter Property="RowHeight" Value="22"/>
  </Style>
  <Style TargetType="DataGridColumnHeader">
    <Setter Property="Background" Value="{DynamicResource BP.Panel}"/><Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="BorderBrush" Value="{DynamicResource BP.Border}"/><Setter Property="BorderThickness" Value="0,0,1,1"/><Setter Property="Padding" Value="8,4"/>
  </Style>
  <Style x:Key="BP.PickerToggle" TargetType="ToggleButton">
    <Setter Property="Foreground" Value="{DynamicResource BP.Text}"/>
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
      <Border x:Name="b" Background="{DynamicResource BP.Input}" BorderBrush="{DynamicResource BP.Border}" BorderThickness="1" CornerRadius="2" Padding="8,3,24,3">
        <Grid><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Left"/>
          <Path HorizontalAlignment="Right" Margin="0,0,-16,0" VerticalAlignment="Center" Data="M0,0 L4,4 L8,0" Stroke="{DynamicResource BP.Text}" StrokeThickness="1.4"/></Grid></Border>
      <ControlTemplate.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
        <Trigger Property="IsChecked" Value="True"><Setter TargetName="b" Property="BorderBrush" Value="{DynamicResource BP.Accent}"/></Trigger>
      </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
  </Style>
  <DataTemplate x:Key="BP.LinkRow">
    <CheckBox IsChecked="{Binding IsChecked,Mode=TwoWay}" Margin="8,4" Foreground="{DynamicResource BP.Text}">
      <TextBlock><Run Text="{Binding OrderText,Mode=OneWay}" Foreground="{DynamicResource BP.Accent}" FontWeight="SemiBold"/><Run Text="{Binding Name,Mode=OneWay}"/></TextBlock>
    </CheckBox>
  </DataTemplate>
</ResourceDictionary>
""";
}

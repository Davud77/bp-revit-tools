using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace BotPlus.Shared;

/// <summary>
/// Маркер заполнения, как в Excel: квадратик в правом нижнем углу текущей ячейки. Потяните его вниз или вверх —
/// значение ячейки копируется в строки диапазона (в порядке, в котором они показаны в таблице, с учётом фильтра и сортировки).
/// </summary>
internal sealed class FillHandle : Adorner
{
    const double Size = 7;
    readonly DataGrid grid;
    readonly Func<DataGridColumn, bool> fillable;
    readonly Action<DataGridColumn, object, List<object>> fill;
    Rect cell = Rect.Empty, target = Rect.Empty;
    DataGridColumn? column;
    object? item;
    bool dragging, attached;
    int sourceIndex, targetIndex;

    internal FillHandle(DataGrid grid, Func<DataGridColumn, bool> fillable, Action<DataGridColumn, object, List<object>> fill) : base(grid)
    {
        this.grid = grid; this.fillable = fillable; this.fill = fill;
        IsHitTestVisible = true; Cursor = Cursors.Cross;
        ToolTip = "Потяните вниз или вверх, чтобы скопировать значение";
    }

    internal void Attach()
    {
        grid.Loaded += (_, _) =>
        {
            if (!attached && AdornerLayer.GetAdornerLayer(grid) is { } layer) { layer.Add(this); attached = true; }
            Update();
        };
        grid.CurrentCellChanged += (_, _) => Update();
        grid.LayoutUpdated += (_, _) => Update();
    }

    internal static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    static T? Descendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Видимая область строк таблицы в координатах таблицы.</summary>
    Rect Area => Descendant<ScrollContentPresenter>(grid) is { } p ? p.TransformToAncestor(grid).TransformBounds(new Rect(p.RenderSize)) : new Rect(grid.RenderSize);

    Rect CellRect(object? rowItem, DataGridColumn? col)
    {
        if (rowItem == null || col == null || grid.ItemContainerGenerator.ContainerFromItem(rowItem) is not DataGridRow row) return Rect.Empty;
        if (col.GetCellContent(row)?.Parent is not DataGridCell c || !c.IsVisible) return Rect.Empty;
        var r = c.TransformToAncestor(grid).TransformBounds(new Rect(c.RenderSize));
        var area = Area;
        return r.Bottom <= area.Top + 1 || r.Top >= area.Bottom - 1 ? Rect.Empty : Rect.Intersect(r, area);
    }

    void Update()
    {
        if (!attached) return;
        Rect r = Rect.Empty;
        if (!dragging)
        {
            var cur = grid.CurrentCell;
            column = cur.Column != null && fillable(cur.Column) ? cur.Column : null;
            item = column == null ? null : cur.Item;
        }
        if (column != null && item != null && grid.Items.Contains(item)) r = CellRect(item, column);
        if (r != cell) { cell = r; InvalidateVisual(); }
    }

    Rect Handle => cell.IsEmpty ? Rect.Empty : new Rect(cell.Right - Size - 1, cell.Bottom - Size - 1, Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        var accent = grid.TryFindResource("BP.Accent") as Brush ?? Brushes.DodgerBlue;
        if (dragging && !target.IsEmpty)
        {
            var range = cell.IsEmpty ? target : Rect.Union(cell, target);
            dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = DashStyles.Dash }, range);
        }
        if (cell.IsEmpty) return;
        dc.DrawRectangle(null, new Pen(accent, 2), cell);
        dc.DrawRectangle(accent, new Pen(Brushes.White, 1), Handle);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        dragging || Handle.Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (item == null || column == null || !Handle.Contains(e.GetPosition(this))) return;
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        sourceIndex = targetIndex = grid.Items.IndexOf(item);
        target = Rect.Empty;
        dragging = sourceIndex >= 0 && CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging || column == null) return;
        var p = e.GetPosition(grid);
        var area = Area;
        if (Descendant<ScrollViewer>(grid) is { } scroll)
        {
            if (p.Y < area.Top) scroll.LineUp();
            else if (p.Y > area.Bottom) scroll.LineDown();
        }
        var probe = new Point(Math.Clamp(cell.IsEmpty ? area.Left + 4 : cell.Left + 2, area.Left + 1, area.Right - 1), Math.Clamp(p.Y, area.Top + 1, area.Bottom - 1));
        if (Ancestor<DataGridRow>(grid.InputHitTest(probe) as DependencyObject) is { } row && grid.ItemContainerGenerator.IndexFromContainer(row) is var index and >= 0)
        {
            targetIndex = index;
            target = CellRect(grid.Items[index], column);
        }
        Update();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!dragging) return;
        Finish(apply: true);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (dragging) Finish(apply: false);
    }

    void Finish(bool apply)
    {
        dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        int from = Math.Min(sourceIndex, targetIndex), to = Math.Max(sourceIndex, targetIndex);
        var source = item; var col = column;
        target = Rect.Empty;
        if (apply && source != null && col != null && from != to)
        {
            var targets = new List<object>();
            for (int i = from; i <= to; i++) if (i != sourceIndex && i < grid.Items.Count) targets.Add(grid.Items[i]!);
            try { fill(col, source, targets); }
            catch (Exception error) { MessageBox.Show(Window.GetWindow(grid), error.Message, "Заполнение ячеек"); }
            grid.SelectedItems.Clear();
            for (int i = from; i <= to && i < grid.Items.Count; i++) grid.SelectedItems.Add(grid.Items[i]);
        }
        Update();
        InvalidateVisual();
    }
}

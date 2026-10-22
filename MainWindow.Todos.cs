using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Helpers;
using DynamicIsland.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

/// <summary>
/// The to-do page: a short list you tick off, plus a box to add to it. The list is saved after every change.
/// <para>
/// Typing needs keyboard focus, which the island normally never takes (WS_EX_NOACTIVATE). So a click on the
/// "Add a to-do" box asks Windows for focus explicitly, and Enter on an empty box, Esc or collapsing the island
/// hands it back to the app you were in. Clicking anywhere else ends typing too, the way any app loses focus.
/// </para>
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<TodoItem> _todos = new();
    private double _todoViewHeight;        // the height the to-do page is at, or springing to
    private bool _typing;                  // the to-do box has keyboard focus
    private IntPtr _focusBeforeTyping;     // the window to give focus back to

    private void InitializeTodos(SavedState saved)
    {
        // state.json is a plain file people may edit by hand, so skip anything empty or malformed.
        foreach (SavedTodo? todo in saved.Todos ?? new List<SavedTodo>())
        {
            if (!string.IsNullOrWhiteSpace(todo?.Text))
                _todos.Add(new TodoItem(todo.Text, todo.Done));
        }
        TodoListView.ItemsSource = _todos;
        _todos.CollectionChanged += OnTodosChanged;
        UpdateTodoLayout();
    }

    private void DisposeTodos() => _todos.CollectionChanged -= OnTodosChanged;

    // ─────────────── The list ───────────────

    private void OnTodosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateTodoLayout();
        SaveState();
    }

    private void AddTodo(string text)
    {
        // New to-dos go below the open ones and above any that are already done.
        int index = 0;
        for (int i = 0; i < _todos.Count; i++)
        {
            if (!_todos[i].IsDone)
                index = i + 1;
        }

        var item = new TodoItem(text);
        _todos.Insert(index, item);

        // Once the new row exists, scroll it into view (the list scrolls beyond IslandConfig.TodoVisibleRows).
        Dispatcher.InvokeAsync(() =>
            (TodoListView.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement)?.BringIntoView(),
            DispatcherPriority.Loaded);
    }

    private void OnTodoToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TodoItem item)
            return;

        // The row restyles itself (see TodoRowTemplate). It stays where it is for now, so it does not jump away
        // under the pointer; finished to-dos move to the bottom the next time the list opens.
        item.IsDone = !item.IsDone;
        UpdateTodoLayout();
        SaveState();
    }

    private void OnTodoDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TodoItem item)
            _todos.Remove(item);
    }

    private void OnClearDoneClick(object sender, RoutedEventArgs e)
    {
        _todos.CollectionChanged -= OnTodosChanged; // one save for the whole batch
        for (int i = _todos.Count - 1; i >= 0; i--)
        {
            if (_todos[i].IsDone)
                _todos.RemoveAt(i);
        }
        _todos.CollectionChanged += OnTodosChanged;
        UpdateTodoLayout();
        SaveState();
    }

    /// <summary>Moves finished to-dos below the open ones, keeping the order within each group.</summary>
    private void SortTodos()
    {
        _todos.CollectionChanged -= OnTodosChanged;
        bool moved = false;
        int next = 0;
        for (int i = 0; i < _todos.Count; i++)
        {
            if (_todos[i].IsDone)
                continue;
            if (i != next)
            {
                _todos.Move(i, next);
                moved = true;
            }
            next++;
        }
        _todos.CollectionChanged += OnTodosChanged;
        if (moved)
            SaveState();
    }

    private void UpdateTodoLayout()
    {
        TodoEmptyText.Visibility = _todos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearDoneButton.Visibility = _todos.Any(todo => todo.IsDone) ? Visibility.Visible : Visibility.Collapsed;
        ResizeTodoView();
    }

    // ─────────────── Height ───────────────

    /// <summary>The page grows a row at a time (at least one row, for the "No to-dos yet" line), up to the scroll limit.</summary>
    private double TodoTargetHeight()
        => IslandConfig.TodoChromeHeight + Math.Clamp(_todos.Count, 1, IslandConfig.TodoVisibleRows) * IslandConfig.TodoRowHeight;

    /// <summary>Sizes the page for its list before it fades in, and returns that height for the morph.</summary>
    private double PrepareTodoView()
    {
        _todoViewHeight = TodoTargetHeight();
        TodoView.BeginAnimation(HeightProperty, null);
        TodoView.Height = _todoViewHeight;
        return _todoViewHeight;
    }

    /// <summary>
    /// When the list grows or shrinks while you look at it, the island and the page spring to the new height
    /// together, so the "Add a to-do" box rides along with the island's bottom edge.
    /// </summary>
    private void ResizeTodoView()
    {
        if (_activeView != TodoView)
        {
            PrepareTodoView();
            return;
        }

        double height = TodoTargetHeight();
        if (height == _todoViewHeight)
            return; // e.g. a to-do was ticked off: same number of rows
        _todoViewHeight = height;

        var resize = new DoubleAnimation(height, MorphDuration) { EasingFunction = SpringCurve };
        Island.BeginAnimation(HeightProperty, resize);
        TodoView.BeginAnimation(HeightProperty, resize);
    }

    // ─────────────── Typing ───────────────

    private void OnTodoInputMouseDown(object sender, MouseButtonEventArgs e) => BeginTyping();

    private void BeginTyping()
    {
        _hoverTimer.Cancel(); // stay open while you type, even if the pointer wanders off
        if (!_typing)
        {
            _typing = true;
            _focusBeforeTyping = NativeMethods.GetForegroundWindow();
            // Allowed because the user just clicked the island. The click itself still doesn't activate it.
            if (!NativeMethods.SetForegroundWindow(_hwnd))
                Log.Error("Windows did not give the island keyboard focus for the to-do box");
        }
        TodoInput.Focus();
    }

    /// <summary>Ends typing. With <paramref name="restoreFocus"/>, the app you were in gets the keyboard back.</summary>
    private void EndTyping(bool restoreFocus)
    {
        if (!_typing)
            return;
        _typing = false;

        Keyboard.ClearFocus();
        if (restoreFocus && _focusBeforeTyping != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _hwnd)
            NativeMethods.SetForegroundWindow(_focusBeforeTyping);
        _focusBeforeTyping = IntPtr.Zero;

        // The pointer may have left while you typed: collapse now, as it would have then.
        if (_expanded && !HitZone.IsMouseOver)
            _hoverTimer.Schedule(IslandConfig.HoverCollapseDelay, () => SetExpanded(false));
    }

    /// <summary>You clicked another window or switched apps: the island lets go of the keyboard.</summary>
    private void OnDeactivated(object? sender, EventArgs e) => EndTyping(restoreFocus: false);

    private void OnTodoInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                string text = TodoInput.Text.Trim();
                if (text.Length == 0)
                {
                    EndTyping(restoreFocus: true);
                    return;
                }
                AddTodo(text);
                TodoInput.Clear(); // ready for the next one
                break;

            case Key.Escape:
                e.Handled = true;
                TodoInput.Clear();
                EndTyping(restoreFocus: true);
                break;

            case Key.Tab:
                e.Handled = true; // nothing else in the island takes focus, so keep it in the box
                break;
        }
    }

    private void OnTodoInputTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => TodoPlaceholder.Visibility = TodoInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
}

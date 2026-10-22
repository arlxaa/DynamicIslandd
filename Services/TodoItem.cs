using System.ComponentModel;

namespace DynamicIsland.Services;

/// <summary>One line of the to-do list. It tells the UI when it is ticked off, so its row restyles itself.</summary>
public sealed class TodoItem : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs IsDoneChanged = new(nameof(IsDone));
    private bool _isDone;

    public TodoItem(string text, bool isDone = false)
    {
        Text = text;
        _isDone = isDone;
    }

    public string Text { get; }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone == value)
                return;
            _isDone = value;
            PropertyChanged?.Invoke(this, IsDoneChanged);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

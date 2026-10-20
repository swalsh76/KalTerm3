using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KalTerm.Terminal.Mvvm;

/// <summary>The base of every view-model: properties that tell the view when they change.</summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a field and raises the change notification when the value differs.</summary>
    /// <returns><see langword="true"/> when the value changed.</returns>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

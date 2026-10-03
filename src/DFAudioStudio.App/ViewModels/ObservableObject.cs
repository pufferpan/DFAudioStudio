using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DFAudioStudio.App.ViewModels;

/// <summary>
/// 手写的 INotifyPropertyChanged 基类（刻意不引入 CommunityToolkit.Mvvm，保持零第三方依赖）。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>值有变化时写入字段并通知，返回是否真的变了。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }
}

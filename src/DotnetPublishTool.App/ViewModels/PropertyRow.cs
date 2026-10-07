using DotnetPublishTool.Core.Models;

namespace DotnetPublishTool.App.ViewModels;

/// <summary>「其他属性」表格中的一行。</summary>
public sealed class PropertyRow : ObservableObject
{
    private string _key = string.Empty;
    private string _value = string.Empty;

    public PropertyRow()
    {
    }

    public PropertyRow(string key, string? value)
    {
        _key = key;
        _value = value ?? string.Empty;
    }

    public string Key
    {
        get => _key;
        set => SetProperty(ref _key, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public ProfileProperty ToProperty() => new(Key, Value);
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace PayloadPanda.Models;

public partial class QueryParamItem : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;
}

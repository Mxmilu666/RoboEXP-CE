using Avalonia.Data;

namespace RoboExp.App.Localization;

/// <summary>
/// <c>{l:Tr File}</c> binds to <see cref="Localizer"/> and follows language switches. <c>Plain=True</c> strips
/// mnemonics (labels, window titles); the default keeps them as Avalonia access keys (menus, buttons).
/// </summary>
public sealed class TrExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";
    public bool Plain { get; set; }

    public BindingBase ProvideValue(IServiceProvider serviceProvider) => new ReflectionBinding($"[{Key}]")
    {
        Source = Plain ? Localizer.Instance.Plain : Localizer.Instance,
        Mode = BindingMode.OneWay,
    };
}

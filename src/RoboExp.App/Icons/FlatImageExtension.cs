using Avalonia.Media;

namespace RoboExp.App.Icons;

/// <summary>Flat toolbar/menu icon, independent of the vendor installation.</summary>
public sealed class FlatImageExtension
{
    public FlatImageExtension() { }
    public FlatImageExtension(string name) => Name = name;
    public string Name { get; set; } = "";
    public IImage ProvideValue(IServiceProvider serviceProvider) => FlatIcons.Command(Name);
}

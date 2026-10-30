using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace RoboExp.App.Properties;

/// <summary>Persistent PropertyGrid-style selection help; never edits the block's Hint.</summary>
public sealed class PropertyHelpView : Border
{
    public PropertyHelpView()
    {
        Name = "PropertyHelp";
        this.Bind(BackgroundProperty, new DynamicResourceExtension("StudioPanelBrush"));
        this.Bind(BorderBrushProperty, new DynamicResourceExtension("StudioBorderBrush"));
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(6);
        MinHeight = 100;
        var name = new SelectableTextBlock { Name = "PropertyHelpName", FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        name.Bind(SelectableTextBlock.ForegroundProperty, new DynamicResourceExtension("StudioTextBrush"));
        name.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(BlockPropertiesViewModel.HelpName)));
        var description = new SelectableTextBlock { Name = "PropertyHelpDescription", TextWrapping = TextWrapping.Wrap };
        description.Bind(SelectableTextBlock.ForegroundProperty, new DynamicResourceExtension("StudioMutedBrush"));
        description.Bind(SelectableTextBlock.TextProperty, new Binding(nameof(BlockPropertiesViewModel.HelpDescription)));
        Child = new ScrollViewer
        {
            MaxHeight = 180,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 4, Children = { name, description } },
        };
    }
}

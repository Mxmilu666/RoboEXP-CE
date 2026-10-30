using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace RoboExp.App;

/// <summary>
/// Maps view models (documents, panel contents) to views. Registered in <see cref="AppComposition"/>; installed as an
/// application-level data template so any <c>ContentControl</c> showing a registered view model renders its view.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, (Func<object, Control> Factory, bool Reuse)> Factories = [];
    private static readonly ConditionalWeakTable<object, Control> Cache = [];

    /// <summary>
    /// <paramref name="reuseView"/>: keep one view per view model for its lifetime (documents — preserves caret, scroll
    /// and canvas state across tab switches). The view must then only be shown in one place at a time.
    /// </summary>
    public static void Register<TViewModel>(Func<TViewModel, Control> factory, bool reuseView = false) where TViewModel : class =>
        Factories[typeof(TViewModel)] = (vm => factory((TViewModel)vm), reuseView);

    public bool Match(object? data) => data != null && Resolve(data.GetType()) != null;

    public Control? Build(object? param)
    {
        if (param == null) return null;
        if (Resolve(param.GetType()) is not { } entry)
            return new TextBlock { Text = "No view registered for " + param.GetType().Name };
        if (entry.Reuse && Cache.TryGetValue(param, out var cached)) return cached;
        var view = entry.Factory(param);
        view.DataContext = param;
        if (entry.Reuse) Cache.AddOrUpdate(param, view);
        return view;
    }

    private static (Func<object, Control> Factory, bool Reuse)? Resolve(Type type)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (Factories.TryGetValue(t, out var f)) return f;
        return null;
    }
}

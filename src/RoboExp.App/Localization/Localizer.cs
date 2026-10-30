using System.ComponentModel;
using System.Text;
using RoboExp.Core.Library;

namespace RoboExp.App.Localization;

/// <summary>
/// UI strings read from built-in original manifest payloads, falling back language → neutral like
/// <c>ResourceManager</c>. No vendor installation is needed. Strings that only exist in the port
/// live in <see cref="PortStrings"/>.
/// <para>Key syntax: <c>File</c> = CWRM (<c>RoboExp.Sanway.CwLocalization</c>); <c>Xwb:Key</c> = XWBRM;
/// <c>Tzw:Key</c> = TZWRM; <c>Port:Key</c> = <see cref="PortStrings"/>; <c>Form:Key</c> = the WinForms resources of
/// <c>RoboExp.Form</c> (e.g. <c>Options:btnOK.Text</c>, <c>Options:$this.Text</c>).</para>
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance { get; } = new();

    private const string IndexerName = "Item[]";

    private readonly Dictionary<string, Tables> _tables = new(StringComparer.OrdinalIgnoreCase);

    private Localizer() => Plain = new PlainView(this);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public UiLanguage Language { get; private set; } = UiLanguage.CHS;

    /// <summary>Same lookups with WinForms mnemonics removed, for labels/titles/messages.</summary>
    public PlainView Plain { get; }

    /// <summary>Menu/button text: WinForms <c>&amp;F</c> mnemonics converted to Avalonia access keys (<c>_F</c>).</summary>
    public string this[string key] => ToAccessText(Raw(key));

    /// <summary>Plain text (mnemonics removed); use from code.</summary>
    public string T(string key) => StripMnemonic(Raw(key));

    public string Raw(string key)
    {
        var colon = key.IndexOf(':');
        if (colon > 0)
        {
            var scope = key[..colon];
            var name = key[(colon + 1)..];
            if (scope == "Port") return PortStrings.Get(name, Language);
            var baseName = scope switch
            {
                "Xwb" => "RoboExp.xuwenbin.XwbLocalization",
                "Tzw" => "RoboExp.Tangzhiwei.TzwLocalization",
                _ => "RoboExp." + scope,
            };
            return Lookup(baseName, name) ?? Lookup(scope, name) ?? PortStrings.TryGet(key, Language) ?? name;
        }
        return Lookup("RoboExp.Sanway.CwLocalization", key) ?? PortStrings.TryGet(key, Language) ?? key;
    }

    /// <summary>Reloads built-in UI tables without an original installation.</summary>
    public void Load()
    {
        _tables.Clear();
        Raise();
    }

    public void SetLanguage(UiLanguage language)
    {
        if (Language == language) return;
        Language = language;
        Raise();
    }

    private void Raise()
    {
        RaiseIndexer(this, PropertyChanged);
        Plain.Raise();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Indexer bindings listen for "Item[]", "Item" or a null name depending on the binding engine; raise all.</summary>
    private static void RaiseIndexer(object sender, PropertyChangedEventHandler? handler)
    {
        if (handler == null) return;
        handler(sender, new PropertyChangedEventArgs(IndexerName));
        handler(sender, new PropertyChangedEventArgs("Item"));
        handler(sender, new PropertyChangedEventArgs(null));
    }


    private string? Lookup(string baseName, string key)
    {
        if (!_tables.TryGetValue(baseName, out var tables))
        {
            tables = new Tables(
                LegacyResources.ReadStringTable(baseName + ".resources"),
                LegacyResources.ReadStringTable(baseName + ".zh-CHS.resources", "CHS"),
                LegacyResources.ReadStringTable(baseName + ".zh-CHT.resources", "CHT"));
            _tables[baseName] = tables;
        }
        var specific = Language switch { UiLanguage.CHS => tables.Chs, UiLanguage.CHT => tables.Cht, _ => null };
        if (specific != null && specific.TryGetValue(key, out var s)) return s;
        return tables.Neutral != null && tables.Neutral.TryGetValue(key, out var n) ? n : null;
    }

    /// <summary><c>"文件(&amp;F)"</c> → <c>"文件(_F)"</c>, <c>"&amp;&amp;"</c> → <c>"&amp;"</c>, literal <c>_</c> escaped as <c>__</c>.</summary>
    public static string ToAccessText(string text)
    {
        if (text.IndexOf('&') < 0 && text.IndexOf('_') < 0) return text;
        var sb = new StringBuilder(text.Length + 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_') sb.Append("__");
            else if (c == '&' && i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; }
            else if (c == '&') sb.Append('_');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Removes WinForms mnemonics: <c>"&amp;Open..."</c> → <c>"Open..."</c>, <c>"打开(&amp;O)..."</c> → <c>"打开..."</c>.</summary>
    public static string StripMnemonic(string text)
    {
        if (text.IndexOf('&') < 0) return text;
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(' && i + 3 < text.Length && text[i + 1] == '&' && text[i + 3] == ')') { i += 3; continue; }
            if (c == '&' && i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; continue; }
            if (c == '&') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed record Tables(
        IReadOnlyDictionary<string, string>? Neutral,
        IReadOnlyDictionary<string, string>? Chs,
        IReadOnlyDictionary<string, string>? Cht);

    public sealed class PlainView(Localizer owner) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string this[string key] => owner.T(key);
        internal void Raise() => RaiseIndexer(this, PropertyChanged);
    }
}

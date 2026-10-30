using Avalonia.Media.Imaging;
using RoboExp.App.Localization;
using RoboExp.Core.Hardware;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Project;

namespace RoboExp.App.Hardware;

/// <summary>
/// Per robot type, what the original DataManager loaded at startup for the hardware UI: the system hardware table
/// (<c>Robots/&lt;TYPE&gt;/Resources/SystemHardwareTable.xml</c>, nick names/descriptions in the UI language) and the
/// controller picture with its pins (<c>RCUInfo.dat</c>). Cached per install/type (table also per language).
/// </summary>
public sealed class HardwareCatalog
{
    private static readonly Dictionary<(string Root, RobotType Type), (RcuBoard? Board, Bitmap? Picture)> Boards = [];
    private static readonly Dictionary<(string Root, RobotType Type, UiLanguage Lang), HardwareTable?> Tables = [];

    private HardwareCatalog(HardwareTable? systemTable, RcuBoard? board, Bitmap? picture)
    {
        SystemTable = systemTable;
        Board = board;
        Picture = picture;
    }

    /// <summary>Null when the type ships no SystemHardwareTable.xml.</summary>
    public HardwareTable? SystemTable { get; }

    /// <summary>Null when the type ships no RCUInfo.dat (instances cannot be placed).</summary>
    public RcuBoard? Board { get; }

    public Bitmap? Picture { get; }

    public static HardwareCatalog Get(VendorInstall install, RobotType type)
    {
        if (!Boards.TryGetValue((install.Root, type), out var board))
        {
            var b = RcuBoard.Load(install, type);
            board = (b, RcuPictureControl.DecodePicture(b));
            Boards[(install.Root, type)] = board;
        }
        var lang = Localizer.Instance.Language;
        if (!Tables.TryGetValue((install.Root, type, lang), out var table))
        {
            table = LibraryProjectInfo.LoadSystemHardwareTable(install, type, lang);
            Tables[(install.Root, type, lang)] = table;
        }
        return new HardwareCatalog(table, board.Board, board.Picture);
    }
}

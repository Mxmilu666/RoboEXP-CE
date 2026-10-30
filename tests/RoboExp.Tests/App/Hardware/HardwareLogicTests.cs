using RoboExp.Core.Hardware;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Project;

namespace RoboExp.Tests.App.Hardware;

public class HardwareLogicTests
{
    private static ArrayList<int> L(params int[] v) => new(v);

    private static HardwareType Type(string name, int pins, bool isDefault = true, int[]? ctrlLimit = null) => new()
    {
        Name = name,
        PinCount = pins,
        IsDefault = isDefault,
        CtrlLimitList = L(ctrlLimit ?? []),
        GNDLimitList = new(),
        VccLimitList = new(),
    };

    private static HardwareInstance Inst(string type, string name, int[] ctrl, int[]? gnd = null, int[]? vcc = null) => new()
    {
        TypeName = type,
        Name = name,
        PinTeam = new PinTeam { CtrlPinNumList = L(ctrl), GNDPinNumList = L(gnd ?? []), VccPinNumList = L(vcc ?? []) },
    };

    private static HardwareTable Table(params HardwareItem[] items)
    {
        var t = new HardwareTable();
        foreach (var i in items) t.HardwareItemList!.Add(i);
        return t;
    }

    private static HardwareItem Item(HardwareType type, params HardwareInstance[] instances) =>
        new() { HardwareType = type, InstanceList = new ArrayList<HardwareInstance>(instances) };

    /// <summary>4 ports A..D: ctrl pins 0..7 (S/D per port), one GND/Vcc per port; pins 6 and 7 are connected.</summary>
    private static RcuBoard Board() => new()
    {
        PinWidth = 10,
        PinHeight = 10,
        CtrlPins = Enumerable.Range(0, 8).Select(i => new BoardPin($"P{i}", i * 10, 0)).ToList(),
        GndPins = Enumerable.Range(0, 4).Select(i => new BoardPin($"G{i}", i * 20, 20)).ToList(),
        VccPins = Enumerable.Range(0, 4).Select(i => new BoardPin($"V{i}", i * 20, 40)).ToList(),
        SamePinList = [[6, 7]],
        OnePinList = Enumerable.Range(0, 4).Select(p => new PinTeamDef([p * 2], [p], [p])).ToList(),
        TwoPinList = Enumerable.Range(0, 4).Select(p => new PinTeamDef([p * 2, p * 2 + 1], [p], [p])).ToList(),
        FourPinList = [new PinTeamDef([0, 1, 2, 3], [0, 1], [0, 1]), new PinTeamDef([4, 5, 6, 7], [2, 3], [2, 3])],
    };

    [Fact]
    public void Default_order_picks_the_team_of_the_hovered_pin()
    {
        var pins = new PinAssignment(Board(), Type("_MOTOR_", 2), Table());
        var team = pins.CandidateAt(PinKind.Ctrl, 3);
        Assert.NotNull(team);
        Assert.Equal([2, 3], team.Ctrl);
        Assert.Equal(team, pins.CandidateAt(PinKind.Gnd, 1));
        Assert.Equal([6, 7], pins.CandidateAt(PinKind.Gnd, 3)!.Ctrl);
        pins.Apply(team);
        Assert.Equal([2, 3], pins.Ctrl);
        Assert.Equal([1], pins.Gnd);
        Assert.Equal([1], pins.Vcc);
        Assert.Null(pins.ValidatePins());
    }

    [Fact]
    public void Pins_of_other_instances_and_limits_are_unavailable()
    {
        var table = Table(Item(Type("_LED_", 1), Inst("_LED_", "_LED_A_", [0], [0], [0])));
        var pins = new PinAssignment(Board(), Type("_MOTOR_", 2, ctrlLimit: [4]), table);
        Assert.True(pins.IsUsed(PinKind.Ctrl, 0));
        Assert.True(pins.IsUsed(PinKind.Gnd, 0));
        Assert.Null(pins.CandidateAt(PinKind.Ctrl, 1));   // team 0,1 shares used ctrl 0
        Assert.Null(pins.CandidateAt(PinKind.Ctrl, 5));   // team 4,5 contains limited pin 4
        Assert.NotNull(pins.CandidateAt(PinKind.Ctrl, 2));
    }

    [Fact]
    public void Edited_instance_does_not_block_its_own_pins()
    {
        var table = Table(Item(Type("_LED_", 1), Inst("_LED_", "_LED_A_", [0], [0], [0])));
        var pins = new PinAssignment(Board(), table.HardwareItemList![0].HardwareType!, table, "_LED_A_", table.HardwareItemList[0].InstanceList![0].PinTeam);
        Assert.False(pins.IsUsed(PinKind.Ctrl, 0));
        Assert.Equal([0], pins.Ctrl);
        Assert.NotNull(pins.CandidateAt(PinKind.Ctrl, 0));
    }

    [Fact]
    public void Same_pins_are_blocked_together()
    {
        var table = Table(Item(Type("_X_", 1, isDefault: false), Inst("_X_", "_X_A_", [7])));
        var pins = new PinAssignment(Board(), Type("_Y_", 2, isDefault: false), table);
        Assert.True(pins.IsUsed(PinKind.Ctrl, 6));
        Assert.Null(pins.CandidateAt(PinKind.Ctrl, 6));
    }

    [Fact]
    public void Manual_order_assigns_single_pins_to_slots_without_duplicates()
    {
        var pins = new PinAssignment(Board(), Type("_Y_", 2, isDefault: false), Table());
        Assert.Equal("HardwareInstFrm_ValueIsNull", pins.ValidatePins());
        Assert.Null(pins.CandidateAt(PinKind.Gnd, 0));
        pins.SelectedSlot = 0;
        pins.Apply(pins.CandidateAt(PinKind.Ctrl, 6)!);
        pins.SelectedSlot = 1;
        Assert.Null(pins.CandidateAt(PinKind.Ctrl, 6));   // already in slot 0
        Assert.Null(pins.CandidateAt(PinKind.Ctrl, 7));   // connected to slot 0's pin
        pins.Apply(pins.CandidateAt(PinKind.Ctrl, 2)!);
        Assert.Equal([6, 2], pins.Ctrl);
        Assert.Empty(pins.Gnd);
        Assert.Null(pins.ValidatePins());
        Assert.Equal(["Pin0: No.P6 pin", "Pin1: No.P2 pin"], pins.SlotCaptions());
    }

    [Fact]
    public void Four_pin_teams_prefer_the_last_available()
    {
        var pins = new PinAssignment(Board(), Type("_Z_", 4), Table());
        Assert.Equal([4, 5, 6, 7], pins.CandidateAt(PinKind.Ctrl, 5)!.Ctrl);
        Assert.Equal([0, 1, 2, 3], pins.CandidateAt(PinKind.Gnd, 1)!.Ctrl);
    }

    [Fact]
    public void HitTest_uses_pin_rectangles_in_ctrl_gnd_vcc_order()
    {
        var b = Board();
        Assert.Equal((PinKind.Ctrl, 1), b.HitTest(15, 5));
        Assert.Equal((PinKind.Gnd, 2), b.HitTest(41, 21));
        Assert.Equal((PinKind.Vcc, 0), b.HitTest(0, 49));
        Assert.Null(b.HitTest(15, 15));
    }

    [Theory]
    [InlineData("", "HardwareInstFrm_NameIsNull")]
    [InlineData("A-1", "HardwareInstFrm_ErrHardwareName")]
    [InlineData("中", "HardwareInstFrm_ErrHardwareName")]
    [InlineData("A", "HardwareInstFrm_SameName")]
    [InlineData("V", "HardwareInstFrm_SameVarName")]
    [InlineData("B2", null)]
    public void Instance_name_rules(string text, string? expected)
    {
        var table = Table(Item(Type("_LED_", 1), Inst("_LED_", "_LED_A_", [0])));
        var vars = new[] { new VarStruct { Name = "_LED_V_" } };
        Assert.Equal(expected, PinAssignment.ValidateInstanceName("_LED_", text, table, vars));
    }

    [Fact]
    public void Instance_name_text_round_trips()
    {
        Assert.Equal("_LED_M1_", PinAssignment.FullInstanceName("_LED_", "M1"));
        Assert.Equal("M1", PinAssignment.InstanceNameText("_LED_", "_LED_M1_"));
        Assert.Equal("", PinAssignment.InstanceNameText("_LED_", "_MOTOR_M1_"));
        Assert.Equal("", PinAssignment.InstanceNameText("_LED_", null));
    }

    [Theory]
    [InlineData("", "HardwareTypeFrm_NameIsNull")]
    [InlineData("A_B", "HardwareTypeFrm_ErrTypeName")]
    [InlineData("LED", "HardwareTypeFrm_SameName")]
    [InlineData("NEW1", null)]
    public void Type_name_rules(string text, string? expected) =>
        Assert.Equal(expected, PinAssignment.ValidateTypeName(text, Table(Item(Type("_LED_", 1)))));

    [Fact]
    public void ToggleLimit_toggles_connected_ctrl_pins_together()
    {
        var limits = new List<int>();
        PinAssignment.ToggleLimit(limits, PinKind.Ctrl, 7, Board());
        Assert.Equal([6, 7], limits);
        PinAssignment.ToggleLimit(limits, PinKind.Ctrl, 6, Board());
        Assert.Empty(limits);
        PinAssignment.ToggleLimit(limits, PinKind.Gnd, 1, Board());
        Assert.Equal([1], limits);
    }

    [Fact]
    public void Combined_table_merges_project_into_system()
    {
        var system = Table(Item(Type("_LED_", 1), Inst("_LED_", "_LED_S_", [0])), Item(Type("_MOTOR_", 2)));
        var project = Table(
            Item(Type("_LED_", 1), Inst("_LED_", "_LED_S_", [2]), Inst("_LED_", "_LED_P_", [4])),
            Item(Type("_MINE_", 1), Inst("_MINE_", "_MINE_A_", [6])));
        project.Description = "proj";

        var combined = HardwareTables.GetCombinedTable(system, project);

        Assert.Equal(["_LED_", "_MOTOR_", "_MINE_"], combined.HardwareItemList!.Select(i => i.HardwareType!.Name));
        var led = combined.HardwareItemList![0].InstanceList!;
        Assert.Equal(["_LED_S_", "_LED_P_"], led.Select(i => i.Name));
        Assert.Equal([2], led[0].PinTeam!.CtrlPinNumList!);   // project replaces same-named instance in place
        Assert.Equal("proj", combined.Description);
        // deep copy: the inputs are untouched
        Assert.NotSame(project.HardwareItemList![1], combined.HardwareItemList[2]);
        Assert.Equal([0], system.HardwareItemList![0].InstanceList![0].PinTeam!.CtrlPinNumList!);
        Assert.Single(system.HardwareItemList[0].InstanceList!);
    }

    [Fact]
    public void Combined_table_handles_missing_sides()
    {
        Assert.Empty(HardwareTables.GetCombinedTable(null, null).HardwareItemList!);
        var only = Table(Item(Type("_LED_", 1)));
        Assert.NotSame(only, HardwareTables.GetCombinedTable(only, null));
        Assert.Single(HardwareTables.GetCombinedTable(null, only).HardwareItemList!);
    }

    [Fact]
    public void Add_and_remove_instance_keep_only_system_types_with_instances()
    {
        var system = Table(Item(Type("_LED_", 1)));
        var project = Table(Item(Type("_MINE_", 1)));

        HardwareTables.AddInstance(system, project, Inst("_LED_", "_LED_A_", [0]));
        Assert.Equal(["_MINE_", "_LED_"], project.HardwareItemList!.Select(i => i.HardwareType!.Name));
        Assert.NotSame(system.HardwareItemList![0].HardwareType, project.HardwareItemList![1].HardwareType);

        HardwareTables.SetInstancePins(system, project, "_LED_A_", new PinTeam { CtrlPinNumList = L(2) });
        Assert.Equal([2], HardwareTables.FindInstance(project, "_LED_A_")!.PinTeam!.CtrlPinNumList!);

        Assert.True(HardwareTables.RemoveInstance(system, project, "_LED_A_"));
        Assert.Equal(["_MINE_"], project.HardwareItemList!.Select(i => i.HardwareType!.Name));

        Assert.False(HardwareTables.RemoveType(system, project, "_LED_"));
        Assert.True(HardwareTables.RemoveType(system, project, "_MINE_"));
        Assert.Empty(project.HardwareItemList!);
    }

    [Fact]
    public void Subroutine_usage_flags_undefined_instances()
    {
        var combined = Table(Item(Type("_LED_", 1), Inst("_LED_", "_LED_A_", [0])));
        var info = new ProjectInfo();
        var used = new UsedHardObject();
        used.HardwareInstanceList!.Add(Inst("_LED_", "_LED_A_", [0]));
        used.HardwareInstanceList.Add(Inst("_LED_", "_LED_B_", [0]));
        used.HardwareInstanceList.Add(Inst("_FAN_", "_FAN_A_", [0]));
        info.SubFuncUsedHardObjectList!.Add(used);

        var usage = HardwareTables.GetSubroutineUsage(info, combined);

        Assert.Equal(2, usage.Count);
        Assert.True(usage[0].IsDefined);
        Assert.Equal([true, false], usage[0].Instances.Select(i => i.IsDefined));
        Assert.False(usage[1].IsDefined);
        Assert.False(usage[1].Instances[0].IsDefined);
    }

    [Fact]
    public void Vendor_x4_board_and_system_table_load()
    {
        VendorInstall install;
        try { install = VendorInstall.Locate(); }
        catch (DirectoryNotFoundException) { return; }
        var type = RobotType.Parse("X4RCU");
        var board = RcuBoard.Load(install, type);
        Assert.NotNull(board);
        Assert.Equal(62, board.CtrlPins.Count);
        Assert.Equal(15, board.PinWidth);
        Assert.NotNull(board.Picture);
        Assert.Equal(26, board.OnePinList.Count);
        Assert.Equal(30, board.TwoPinList.Count);
        Assert.Equal("A1Sx", board.CtrlPins[0].Name);

        var system = LibraryProjectInfo.LoadSystemHardwareTable(install, type);
        Assert.NotNull(system);
        var motor = HardwareTables.FindItem(system, "_MOTOR_");
        Assert.NotNull(motor);
        var pins = new PinAssignment(board, motor.HardwareType!, HardwareTables.GetCombinedTable(system, new HardwareTable()));
        Assert.Contains(board.TwoPinList, t => pins.IsTeamAvailable(t));
    }
}

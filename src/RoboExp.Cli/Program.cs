using RoboExp.Cli;

if (args.Length == 0)
{
    Console.Error.WriteLine("""
        usage: roboexp <command> [args]
          rcu ...      project file commands (RcuCommands)
          build ...    compile commands (BuildCommands)
          device ...   download commands (DeviceCommands)
        """);
    return 2;
}

var rest = args[1..];
return args[0] switch
{
    "rcu" => RcuCommands.Run(rest),
    "build" => BuildCommands.Run(rest),
    "device" => DeviceCommands.Run(rest),
    _ => Fail($"unknown command '{args[0]}'"),
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

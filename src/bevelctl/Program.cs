using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Bevel.Interop.Cli;

// Validate locally first, so bad args are exit 2 without needing a running shell (08-os-interop §3.2).
var (_, error) = BevelCtlParser.Parse(args);
if (error is not null)
{
    Console.Error.WriteLine(error);
    return ExitCodes.BadArgs;
}

try
{
    var result = await AutomationSocketClient.SendAsync(AutomationSocket.DefaultPath, args);
    if (result.Output.Length > 0)
    {
        if (result.ExitCode == ExitCodes.Ok) Console.WriteLine(result.Output);
        else Console.Error.WriteLine(result.Output);
    }
    return result.ExitCode;
}
catch (SocketException)
{
    Console.Error.WriteLine("bevel: the Bevel shell isn't running (couldn't connect to its socket).");
    return ExitCodes.ShellNotRunning;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"bevel: {ex.Message}");
    return ExitCodes.ShellNotRunning;
}

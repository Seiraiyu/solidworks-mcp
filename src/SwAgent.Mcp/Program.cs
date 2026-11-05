using System;
using System.Text;
using System.Threading;
using SwAgent.Mcp;

Console.OutputEncoding = new UTF8Encoding(false);
using var stop = new CancellationTokenSource();
if (args.Length > 0 && args[0] == "events")
{
    // Ctrl+C ends `events --follow` cleanly; every other command it ends at once, as before.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };
}
return await Cli.RunAsync(args, new CliEnvironment { Stop = stop.Token });

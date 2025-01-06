using NonsensicalPatch.Core;
using NonsensicalPatchUpdater;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

bool debug = false;
#if DEBUG
debug = true;
#endif
var updater = new Updater(args, debug);
updater.Run();

while (!updater.IsEnd)
{
    Thread.Sleep(100);
}

if (debug) Console.ReadKey();
Environment.Exit(0);
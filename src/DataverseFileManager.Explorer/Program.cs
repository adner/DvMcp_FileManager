using DataverseFileManager;
using DataverseFileManager.Explorer;

// Read-only Explorer host (milestone 4A).
//
//   run          (default) authenticate → register sync root → connect cfapi → serve until Enter
//   register     register the sync root only (branded nav-pane node, no content)
//   unregister   remove the sync root
//
// Must run with package identity (installed via Package/setup-sparse-package.ps1); otherwise
// StorageProviderSyncRootManager.Register / cfapi fail. First run triggers interactive OAuth.

string syncRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dataverse");
string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "dvicon.ico");

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";

Log.Init();
Log.Info($"command='{command}', syncRoot='{syncRoot}'");
if (Log.FilePath is not null)
    Console.WriteLine($"Log file: {Log.FilePath}");

try
{
    switch (command)
    {
        case "register":
            await SyncRootRegistrar.RegisterAsync(syncRoot, iconPath);
            break;

        case "unregister":
            SyncRootRegistrar.Unregister();
            break;

        case "run":
            await RunAsync(syncRoot, iconPath);
            break;

        default:
            Console.WriteLine("Usage: DataverseFileManager.Explorer [run|register|unregister]");
            break;
    }
}
catch (Exception ex)
{
    Log.Error($"'{command}' failed", ex);
    throw;
}
finally
{
    Log.Close();
}

static async Task RunAsync(string syncRoot, string iconPath)
{
    // Connection settings come from environment variables or appsettings.json (see appsettings.example.json).
    var options = DataverseConfig.Load();

    Log.Info($"Connecting to {options.McpEndpoint} ...");
    await using var fs = await DataverseFileSystem.CreateAsync(options);

    // Warm-up call on the main thread so the interactive OAuth browser prompt happens HERE,
    // before any background hydration callback (which can't show a browser) needs a token.
    Log.Info("Authenticating (a browser window may open) ...");
    IReadOnlyList<FileItem> root = await fs.ListFolderAsync("/");
    Log.Info($"Authenticated. Root has {root.Count} item(s).");

    await SyncRootRegistrar.RegisterAsync(syncRoot, iconPath);

    CloudProvider.Connect(syncRoot, fs);
    Log.Info("Serving. Browse the 'Dataverse' node in Explorer.");
    Console.WriteLine("Press Enter to stop and disconnect.");
    Console.ReadLine();

    CloudProvider.Disconnect();
    Log.Info("Disconnected.");
}

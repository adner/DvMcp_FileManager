using System.Text;
using DataverseFileManager;

// Dataverse MCP File Manager — manual harness.
//
//   (no args)              run the full end-to-end smoke test (self-cleaning)
//   list   [path]          list a folder
//   tree   [path]          recursively print a subtree
//
// First connect triggers the interactive OAuth (system browser + loopback redirect).

// Connection settings come from environment variables or appsettings.json (see appsettings.example.json).
var options = DataverseConfig.Load();

Console.WriteLine($"Connecting to {options.McpEndpoint} ...");
await using var fs = await DataverseFileSystem.CreateAsync(options);
Console.WriteLine("Connected.\n");

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "smoke";

switch (command)
{
    case "list":
        await ListAsync(fs, args.Length > 1 ? args[1] : "/");
        return 0;

    case "tree":
        await TreeAsync(fs, args.Length > 1 ? args[1] : "/", 0);
        return 0;

    default:
        return await SmokeTestAsync(fs);
}

static async Task ListAsync(IDataverseFileSystem fs, string path)
{
    Console.WriteLine($"Listing '{path}':");
    foreach (var item in await fs.ListFolderAsync(path))
        Console.WriteLine($"  {(item.IsFolder ? "[DIR] " : "      ")}{item.Name,-32}" +
                          (item.IsFolder ? "" : $"{item.SizeBytes,10:N0} bytes"));
}

static async Task TreeAsync(IDataverseFileSystem fs, string path, int depth)
{
    foreach (var item in await fs.ListFolderAsync(path))
    {
        Console.WriteLine($"{new string(' ', depth * 2)}{(item.IsFolder ? "[DIR] " : "- ")}{item.Name}");
        if (item.IsFolder) await TreeAsync(fs, item.Path, depth + 1);
    }
}

static async Task<int> SmokeTestAsync(IDataverseFileSystem fs)
{
    var runner = new TestRunner();
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

    // Isolated, unique workspace so reruns never collide.
    var root = $"/fmtest-{stamp}";
    var nested = $"{root}/alpha/beta";
    var remoteFile = $"{nested}/sample.txt";

    var content = $"Round-trip test {stamp}\n" +
                  "The quick brown fox jumps over the lazy dog.\n" +
                  "Line A\nLine B\nLine C\n";
    var expectedBytes = Encoding.UTF8.GetBytes(content);

    var localUpload = Path.Combine(Path.GetTempPath(), $"fmtest-up-{stamp}.txt");
    var localDownload = Path.Combine(Path.GetTempPath(), $"fmtest-down-{stamp}.txt");
    await File.WriteAllTextAsync(localUpload, content);

    Console.WriteLine($"Test workspace: {root}\n");

    try
    {
        await runner.Check("CreateFolderAsync creates nested folders + ancestors", async () =>
        {
            var folder = await fs.CreateFolderAsync(nested);
            TestRunner.Assert(folder.IsFolder, "returned item should be a folder");
            TestRunner.Assert(folder.Path == nested, $"path should be {nested}, was {folder.Path}");

            foreach (var ancestor in new[] { root, $"{root}/alpha", nested })
            {
                var item = await fs.GetItemAsync(ancestor);
                TestRunner.Assert(item is { IsFolder: true }, $"ancestor '{ancestor}' should exist as a folder");
            }
        });

        await runner.Check("CreateFolderAsync is idempotent", async () =>
        {
            var first = await fs.GetItemAsync(nested);
            var second = await fs.CreateFolderAsync(nested);
            TestRunner.Assert(first!.RecordId == second.RecordId, "re-creating should return the same record");
        });

        await runner.Check("UploadAsync uploads a file with metadata", async () =>
        {
            var uploaded = await fs.UploadAsync(localUpload, remoteFile);
            TestRunner.Assert(!uploaded.IsFolder, "uploaded item should be a file");
            TestRunner.Assert(uploaded.Path == remoteFile, "uploaded path mismatch");
            TestRunner.Assert(uploaded.Extension == ".txt", $"extension should be .txt, was {uploaded.Extension}");
            TestRunner.Assert(uploaded.SizeBytes == expectedBytes.Length,
                $"size should be {expectedBytes.Length}, was {uploaded.SizeBytes}");
        });

        await runner.Check("GetItemAsync resolves the uploaded file", async () =>
        {
            var item = await fs.GetItemAsync(remoteFile);
            TestRunner.Assert(item is not null, "file should resolve");
            TestRunner.Assert(!item!.IsFolder, "should be a file");
            TestRunner.Assert(item.SizeBytes == expectedBytes.Length, "size mismatch on GetItem");
        });

        await runner.Check("GetItemAsync returns null for a missing path", async () =>
        {
            var missing = await fs.GetItemAsync($"{nested}/does-not-exist.txt");
            TestRunner.Assert(missing is null, "missing item should be null");
        });

        await runner.Check("ListFolderAsync lists the file in its folder", async () =>
        {
            var children = await fs.ListFolderAsync(nested);
            var file = children.FirstOrDefault(c => c.Path == remoteFile);
            TestRunner.Assert(file is not null, "sample.txt should be listed");
            TestRunner.Assert(file!.SizeBytes == expectedBytes.Length, "listed size mismatch");
        });

        await runner.Check("ListFolderAsync lists subfolders (folders first)", async () =>
        {
            var children = await fs.ListFolderAsync($"{root}/alpha");
            var beta = children.FirstOrDefault(c => c.Path == nested);
            TestRunner.Assert(beta is { IsFolder: true }, "'beta' subfolder should be listed");
        });

        await runner.Check("DownloadAsync produces a byte-identical file", async () =>
        {
            await fs.DownloadAsync(remoteFile, localDownload);
            var downloaded = await File.ReadAllBytesAsync(localDownload);
            TestRunner.Assert(downloaded.SequenceEqual(expectedBytes),
                $"downloaded bytes ({downloaded.Length}) differ from original ({expectedBytes.Length})");
        });

        await runner.Check("OpenReadAsync streams the correct content", async () =>
        {
            await using var stream = await fs.OpenReadAsync(remoteFile);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            TestRunner.Assert(text == content, "streamed content does not match");
        });

        await runner.Check("UploadAsync to an existing path replaces content in place (no duplicate)", async () =>
        {
            var before = await fs.GetItemAsync(remoteFile);
            TestRunner.Assert(before is not null, "file should exist before re-upload");

            var edited = content + $"Edited at {DateTime.UtcNow:O}\nappended line\n";
            var editedBytes = Encoding.UTF8.GetBytes(edited);
            var localEdit = Path.Combine(Path.GetTempPath(), $"fmtest-edit-{stamp}.txt");
            await File.WriteAllTextAsync(localEdit, edited);
            try
            {
                var reuploaded = await fs.UploadAsync(localEdit, remoteFile);
                TestRunner.Assert(reuploaded.RecordId == before!.RecordId,
                    "re-upload should reuse the same record, not create a new one");
                TestRunner.Assert(reuploaded.SizeBytes == editedBytes.Length,
                    $"size should update to {editedBytes.Length}, was {reuploaded.SizeBytes}");

                var matches = (await fs.ListFolderAsync(nested)).Count(c => c.Path == remoteFile);
                TestRunner.Assert(matches == 1, $"exactly one record should exist at the path, found {matches}");

                await fs.DownloadAsync(remoteFile, localDownload);
                var roundTrip = await File.ReadAllBytesAsync(localDownload);
                TestRunner.Assert(roundTrip.SequenceEqual(editedBytes), "downloaded content should be the edited bytes");
            }
            finally { TryDelete(localEdit); }
        });

        await runner.Check("RenameAsync renames a file in place (extension follows)", async () =>
        {
            await fs.RenameAsync(remoteFile, "renamed.md");
            TestRunner.Assert(await fs.GetItemAsync(remoteFile) is null, "old path should be gone");
            var item = await fs.GetItemAsync($"{nested}/renamed.md");
            TestRunner.Assert(item is { IsFolder: false }, "renamed file should exist");
            TestRunner.Assert(item!.Extension == ".md", $"extension should be .md, was {item.Extension}");
        });

        await runner.Check("MoveAsync moves a file to another folder", async () =>
        {
            var src = $"{nested}/renamed.md";
            var dst = $"{root}/alpha/renamed.md";
            await fs.MoveAsync(src, dst);
            TestRunner.Assert(await fs.GetItemAsync(src) is null, "source should be gone");
            TestRunner.Assert(await fs.GetItemAsync(dst) is { IsFolder: false }, "moved file should exist at destination");
        });

        await runner.Check("RenameAsync on a folder relocates its whole subtree", async () =>
        {
            // /root/alpha holds beta/ and renamed.md → rename alpha to delta; descendants must follow.
            await fs.RenameAsync($"{root}/alpha", "delta");
            TestRunner.Assert(await fs.GetItemAsync($"{root}/alpha") is null, "old folder path should be gone");
            TestRunner.Assert(await fs.GetItemAsync($"{root}/delta") is { IsFolder: true }, "renamed folder should exist");
            TestRunner.Assert(await fs.GetItemAsync($"{root}/delta/renamed.md") is { IsFolder: false }, "child file should follow");
            TestRunner.Assert(await fs.GetItemAsync($"{root}/delta/beta") is { IsFolder: true }, "child folder should follow");
        });

        await runner.Check("DeleteAsync recursively removes the workspace", async () =>
        {
            await fs.DeleteAsync(root);
            TestRunner.Assert(await fs.GetItemAsync(root) is null, "root should be gone");
            TestRunner.Assert(await fs.GetItemAsync(remoteFile) is null, "file should be gone");
            TestRunner.Assert((await fs.ListFolderAsync(root)).Count == 0, "root listing should be empty");
        });
    }
    finally
    {
        // Best-effort cleanup if an assertion aborted before the delete step.
        try { if (await fs.GetItemAsync(root) is not null) await fs.DeleteAsync(root); } catch { /* ignore */ }
        TryDelete(localUpload);
        TryDelete(localDownload);
    }

    return runner.Summarize();
}

static void TryDelete(string path)
{
    try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
}

/// <summary>Tiny pass/fail test runner for the manual harness.</summary>
sealed class TestRunner
{
    private int _passed;
    private int _failed;

    public async Task Check(string name, Func<Task> body)
    {
        Console.Write($"• {name} ... ");
        try
        {
            await body();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("PASS");
            Console.ResetColor();
            _passed++;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAIL");
            Console.ResetColor();
            Console.WriteLine($"    {ex.Message}");
            _failed++;
        }
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public int Summarize()
    {
        Console.WriteLine();
        Console.WriteLine($"==== {_passed} passed, {_failed} failed ====");
        return _failed == 0 ? 0 : 1;
    }
}

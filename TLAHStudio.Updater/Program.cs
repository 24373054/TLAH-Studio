using System.Diagnostics;

namespace TLAHStudio.Updater;

/// <summary>
/// Minimal update helper. Called by the main app when a new version
/// installer has been downloaded and verified.
///
/// Usage: TLAHStudio.Updater.exe <installerPath> <appPid> [installDir]
///
/// Flow:
/// 1. Wait for the main app process to exit (timeout 30s)
/// 2. Run the installer silently
/// 3. Re-launch the main app
/// 4. Exit
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: TLAHStudio.Updater.exe <installerPath> <appPid>");
            return 1;
        }

        string installerPath = args[0];
        if (!int.TryParse(args[1], out int appPid))
        {
            Console.Error.WriteLine($"Invalid PID: {args[1]}");
            return 1;
        }
        var installDir = args.Length >= 3 && !string.IsNullOrWhiteSpace(args[2])
            ? args[2]
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "TLAH Studio");

        Console.WriteLine("TLAH Studio Updater");
        Console.WriteLine($"Installer: {installerPath}");
        Console.WriteLine($"App PID: {appPid}");
        Console.WriteLine($"Install dir: {installDir}");

        // 1. Wait for main app to exit
        try
        {
            using var process = Process.GetProcessById(appPid);
            Console.WriteLine("Waiting for main app to exit...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cts.Token);
            Console.WriteLine("Main app exited.");
        }
        catch (ArgumentException)
        {
            Console.WriteLine("App process already exited.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error waiting for app: {ex.Message}");
        }

        // 2. Verify installer exists
        if (!File.Exists(installerPath))
        {
            Console.Error.WriteLine($"Installer not found: {installerPath}");
            return 1;
        }

        // 3. Run installer silently
        Console.WriteLine("Running installer silently...");
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLAH Studio",
            "logs",
            $"installer-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOLAUNCH /DIR=\"{installDir}\" /LOG=\"{logPath}\"",
            UseShellExecute = true,
            // Verb = "runas"  // Only needed if installing to Program Files
        };

        try
        {
            var installerProc = Process.Start(psi);
            if (installerProc != null)
            {
                using var cts2 = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await installerProc.WaitForExitAsync(cts2.Token);
                Console.WriteLine($"Installer exited with code: {installerProc.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Installer failed: {ex.Message}");
            return 1;
        }

        // 4. Re-launch the main app
        string appPath = Path.Combine(installDir, "TLAHStudio.App.exe");

        if (File.Exists(appPath))
        {
            Console.WriteLine("Launching updated app...");
            Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = true
            });
        }
        else
        {
            Console.Error.WriteLine($"Updated app not found at: {appPath}");
            return 1;
        }

        Console.WriteLine("Update complete.");
        return 0;
    }
}

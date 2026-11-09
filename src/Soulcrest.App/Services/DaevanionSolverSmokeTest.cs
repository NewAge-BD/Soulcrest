using System.Text.Json;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Services;

/// <summary>Headless publish verification; never loads settings, user data, UI or capture.</summary>
internal static class DaevanionSolverSmokeTest
{
    internal static int Run(string? output)
    {
        var exit = 3;
        object report;
        try
        {
            var bundled = DaevanionNativeRuntime.LoadBundledRuntime();
            var request = DaevanionPlanningDefaults.CreateRequest(DaevanionCatalog.Default.Classes[0].Id)
                with { Budgets = new(3, 0, 0) };
            var result = new DaevanionOptimizer().OptimizeAsync(request).GetAwaiter().GetResult();
            var valid = DaevanionPlanValidator.Validate(request, result) is null;
            var used = result.Boards.Sum(board => board.Cost);
            exit = bundled && result.Optimal && valid && used == 3 ? 0 : 3;
            report = new { ExitCode = exit, BundledVcRuntime = bundled, NativeSolver = result.Status.ToString(), Valid = valid, Used = used };
        }
        catch (Exception error)
        { report = new { ExitCode = exit, Error = error.GetType().Name, Message = error.GetBaseException().Message }; }
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(output))
        {
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }
        Console.WriteLine(json);
        return exit;
    }
}

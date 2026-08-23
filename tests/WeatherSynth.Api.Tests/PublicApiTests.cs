using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using WeatherSynth;
using Xunit;

namespace WeatherSynthApiTests;

/// <summary>
/// The contract. <c>PublicApi.approved.txt</c> is the whole of what this library promises, and this
/// test is the thing that makes changing it a decision rather than an accident.
///
/// <para><b>A failure here is not necessarily a bug.</b> Adding a type or a method is a perfectly
/// good change; the point is that it has to be made deliberately and shows up in a diff. Read the
/// reported difference, and if it is what you meant, copy the emitted <c>PublicApi.received.txt</c>
/// over the approved file. If something has <i>vanished</i> from the list, stop: that is a breaking
/// change to a published package, and the usual cause is a type quietly following a dependency into
/// <c>internal</c>.</para>
/// </summary>
public class PublicApiTests
{
    [Fact]
    public void Public_surface_matches_the_approved_list()
    {
        string rendered = PublicApiRenderer.Render(
            typeof(SolarSite).Assembly,
            typeof(SyntheticSolarProvider).Assembly
        );

        string approvedPath = Path.Combine(AppContext.BaseDirectory, "PublicApi.approved.txt");
        string receivedPath = Path.Combine(AppContext.BaseDirectory, "PublicApi.received.txt");

        string approved = File.Exists(approvedPath)
            ? File.ReadAllText(approvedPath).Replace("\r\n", "\n")
            : "";

        if (rendered == approved)
        {
            if (File.Exists(receivedPath))
                File.Delete(receivedPath);

            return;
        }

        File.WriteAllText(receivedPath, rendered);

        string diff = Diff(approved, rendered);

        Assert.Fail(
            "The public API surface has changed.\n\n"
                + diff
                + $"\n\nFull rendering written to {receivedPath}.\n"
                + "If the change is intended, copy it over tests/WeatherSynth.Api.Tests/"
                + "PublicApi.approved.txt. Lines marked '-' are removals, which break callers."
        );
    }

    /// <summary>
    /// Line-level set difference rather than a real diff: the rendering is sorted, so "what
    /// appeared" and "what vanished" is all the information a reviewer needs, and it survives a
    /// member moving between types.
    /// </summary>
    private static string Diff(string approved, string rendered)
    {
        var before = approved.Split('\n');
        var after = rendered.Split('\n');

        var removed = before.Except(after).Where(l => l.Length > 0).ToList();
        var added = after.Except(before).Where(l => l.Length > 0).ToList();

        var report = new List<string>();
        report.AddRange(removed.Select(l => "- " + l.Trim()));
        report.AddRange(added.Select(l => "+ " + l.Trim()));

        return string.Join('\n', report);
    }

    /// <summary>
    /// The fitting machinery must not be reachable from outside. Named explicitly rather than left
    /// to the approved list, because these are the types the refactor existed to hide - the ones
    /// whose serialization, parameterisation and numerics have to stay changeable.
    /// </summary>
    [Theory]
    [InlineData("WeatherSynth.Climate.ClearSkyIndexModel")]
    [InlineData("WeatherSynth.Climate.WindSpeedModel")]
    [InlineData("WeatherSynth.Climate.MonthlyCoupling")]
    [InlineData("WeatherSynth.Climate.LatentAr1Chain")]
    [InlineData("WeatherSynth.Climate.CoupledLatentAr1Chain")]
    [InlineData("WeatherSynth.Climate.ScaledBeta")]
    [InlineData("WeatherSynth.Climate.Weibull")]
    [InlineData("WeatherSynth.Climate.Gaussian")]
    [InlineData("WeatherSynth.Climate.SeriesStatistics")]
    [InlineData("WeatherSynth.Solar.ClearSkyIneichen")]
    [InlineData("WeatherSynth.Solar.DailyClearSkyCalculator")]
    [InlineData("WeatherSynth.Solar.LinkeTurbidity")]
    [InlineData("WeatherSynth.Wind.IntradayShapeModel")]
    [InlineData("WeatherSynth.Data.DwdSolarReader")]
    [InlineData("WeatherSynth.Data.DwdWindReader")]
    [InlineData("WeatherSynth.Data.RepositoryData")]
    public void Machinery_is_not_exported(string typeName)
    {
        var assemblies = new[]
        {
            typeof(SolarSite).Assembly,
            typeof(SyntheticSolarProvider).Assembly,
        };

        // Present in the assembly, so a rename here is caught rather than silently passing ...
        assemblies
            .Select(a => a.GetType(typeName))
            .Should()
            .Contain(t => t != null, $"{typeName} should still exist");

        // ... and not on the exported surface.
        assemblies
            .SelectMany(a => a.GetExportedTypes())
            .Should()
            .NotContain(t => t.FullName == typeName);
    }

    /// <summary>
    /// A published package must not hand out internals to anything it ships beside. The three
    /// grants that exist are the two test projects and the sample, none of which is packaged - and
    /// this assembly is deliberately not among them, which is the whole reason it can act as a
    /// consumer at all.
    /// </summary>
    [Fact]
    public void This_assembly_has_no_internals_access()
    {
        foreach (
            var assembly in new[]
            {
                typeof(SolarSite).Assembly,
                typeof(SyntheticSolarProvider).Assembly,
            }
        )
        {
            assembly
                .GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(a => a.AssemblyName)
                .Should()
                .NotContain("WeatherSynthApiTests")
                .And.NotContain("WeatherSynth.Api.Tests");
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;


[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[GitHubActions(nameof(Build), 
    GitHubActionsImage.WindowsLatest, 
    InvokedTargets = new[] { nameof(Test), nameof(Analysis), nameof(Publish) },
    On = new GitHubActionsTrigger[]
    {
        GitHubActionsTrigger.Push
    })]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    const string _trxExtension = ".trx";
    const string _xmlExtension = ".xml";
    const string _binGlob = "**/bin";
    const string _objGlob = "**/obj";
    readonly string XmlGlob = $"*{_xmlExtension}";
    readonly string TrxGlob = $"*{_trxExtension}";
    const string _dotNet3Moniker = "netcoreapp3.0";
    const string _generatedCodeGlob = "*Generated.cs";
    const string _analyisArtifact = "inspectCode.xml";
    const string _cyclomaticComplexityPluginId = "PowerToys.CyclomaticComplexity";

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    [GitRepository] readonly GitRepository GitRepository;

    Project WebApiProject => Solution.GetProject("Template.WebApi");

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath TestResultDirectory => OutputDirectory / "test-results";

    string CoverageReportDirectory => OutputDirectory / "coverage-report";
    string CoverageReportArchive => OutputDirectory / "coverage-report.zip";

    [CI] readonly AzurePipelines AzurePipelines;
 
    [Partition(2)] readonly Partition TestPartition;

    IEnumerable<Project> TestProjects => TestPartition.GetCurrent(Solution.GetProjects("*.Tests"));

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories(_binGlob, _objGlob).ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories(_binGlob, _objGlob).ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetNoRestore(InvokedTargets.Contains(Restore))
                .SetConfiguration(Configuration));


        });

    Target Publish => _ => _
        .DependsOn(Test)
        .Executes(() =>
        {
            var publishConfigurations =
                from project in new[] { WebApiProject }
                from framework in project.GetTargetFrameworks()
                select new { project, framework };

            DotNetPublish(_ => _
                    .SetNoRestore(InvokedTargets.Contains(Restore))
                    .SetConfiguration(Configuration)
                    .CombineWith(publishConfigurations, (_, v) => _
                        .SetProject(v.project)
                        .SetFramework(v.framework)),
                degreeOfParallelism: 10);
        });

    Target Analysis => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            InspectCode(_ => _
                .SetTargetPath(Solution)
                .SetOutput(OutputDirectory / _analyisArtifact)
                .AddPlugin(_cyclomaticComplexityPluginId, InspectCodePluginLatest));
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Produces(TestResultDirectory / TrxGlob)
        .Produces(TestResultDirectory / XmlGlob)
        .Partition(() => TestPartition)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetConfiguration(Configuration)
                .SetNoBuild(InvokedTargets.Contains(Compile))
                .ResetVerbosity()
                .SetResultsDirectory(TestResultDirectory)
                .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                    .EnableCollectCoverage()
                    .SetCoverletOutputFormat(CoverletOutputFormat.cobertura)
                    .SetExcludeByFile(_generatedCodeGlob)
                    .When(IsServerBuild, _ => _
                        .EnableUseSourceLink()))
                .CombineWith(TestProjects, (_, v) => _
                    .SetProjectFile(v)
                    .SetLogger($"trx;LogFileName={v.Name}{_trxExtension}")
                    .When(InvokedTargets.Contains(Coverage) || IsServerBuild, _ => _
                        .SetCoverletOutput(TestResultDirectory / $"{v.Name}{_xmlExtension}"))));

            TestResultDirectory.GlobFiles(TrxGlob).ForEach(x =>
                AzurePipelines?.PublishTestResults(
                    type: AzurePipelinesTestResultsType.VSTest,
                    title: $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.StageDisplayName})",
                    files: new string[] { x }));
        });

    Target Coverage => _ => _
        .DependsOn(Test)
        .TriggeredBy(Test)
        .Consumes(Test, TestResultDirectory / XmlGlob)
        .Produces(CoverageReportArchive)
        .Executes(() =>
        {
            ReportGenerator(_ => _
                .SetReports(TestResultDirectory / XmlGlob)
                .SetReportTypes(ReportTypes.HtmlInline)
                .SetTargetDirectory(CoverageReportDirectory)
                .SetFramework(_dotNet3Moniker));

            TestResultDirectory.GlobFiles(XmlGlob).ForEach(x =>
                AzurePipelines?.PublishCodeCoverage(
                    AzurePipelinesCodeCoverageToolType.Cobertura,
                    x,
                    CoverageReportDirectory));

            CompressZip(
                directory: CoverageReportDirectory,
                archiveFile: CoverageReportArchive,
                fileMode: FileMode.Create);
        });
}

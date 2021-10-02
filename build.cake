#addin nuget:?package=Cake.DocFx&version=1.0.0
#addin nuget:?package=Cake.Codecov&version=1.0.1

#tool dotnet:?package=GitVersion.Tool&version=5.7.0
#tool dotnet:?package=dotnet-reportgenerator-globaltool&version=4.8.13
#tool nuget:?package=Codecov&version=1.13.0

///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument("Target", "Default");
var tag = Argument("Tag", "cake");

var configuration = HasArgument("Configuration")
    ? Argument<string>("Configuration")
    : EnvironmentVariable("CONFIGURATION") != null
        ? EnvironmentVariable("CONFIGURATION")
        : "Release";

var buildNumber = HasArgument("BuildNumber")
    ? Argument<int>("BuildNumber")
    : AppVeyor.IsRunningOnAppVeyor
        ? AppVeyor.Environment.Build.Number
        : TravisCI.IsRunningOnTravisCI
            ? TravisCI.Environment.Build.BuildNumber
            : EnvironmentVariable("BUILD_NUMBER") != null
                ? int.Parse(EnvironmentVariable("BUILD_NUMBER"))
                : 0;

///////////////////////////////////////////////////////////////////////////////
// GLOBALS
///////////////////////////////////////////////////////////////////////////////

var isAppVeyor = AppVeyor.IsRunningOnAppVeyor;
var isTravis = TravisCI.IsRunningOnTravisCI;
var isCi = isAppVeyor || isTravis;
var isWindows = IsRunningOnWindows();
var cover = isAppVeyor || HasArgument("Cover");

IEnumerable<string> ReadCoverageFilters(string path)
{
    return System.IO.File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#"));
}

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

var solution = "./Colore.sln";
var mainProject = "./src/Colore/Colore.csproj";
var testProject = "./tests/Colore.Tests/Colore.Tests.csproj";
var frameworks = new List<string>();

GitVersion version = null;

Setup(ctx =>
{
    var envVars = EnvironmentVariables();
    Information("Environment variables:");
    foreach (var kvp in envVars.OrderBy(kvp => kvp.Key))
    {
        Information("{0}={1}", kvp.Key, kvp.Value);
    }

    var docFxBranch = EnvironmentVariable("DOCFX_SOURCE_BRANCH_NAME");
    if (docFxBranch != null)
        Information("DocFx branch is {0}", docFxBranch);
    if (isTravis)
        Information("Travis branch is {0}", EnvironmentVariable("TRAVIS_BRANCH"));

    Information("Reading framework settings");

    var xmlValue = XmlPeek(mainProject, "/Project/PropertyGroup/TargetFrameworks");
    if (string.IsNullOrEmpty(xmlValue))
    {
        xmlValue = XmlPeek(mainProject, "/Project/PropertyGroup/TargetFramework");
    }

    if (isWindows)
        frameworks.AddRange(xmlValue.Split(';'));
    else
        frameworks.AddRange(xmlValue.Split(';').Where(v => !v.StartsWith("net4")));

    Information("Frameworks: {0}", string.Join(", ", frameworks));

    if (isCi)
    {
        GitVersion(new GitVersionSettings
        {
            RepositoryPath = ".",
            OutputType = GitVersionOutput.BuildServer
        });
    }

    version = GitVersion(new GitVersionSettings { RepositoryPath = "." });

    Information("Version: {0} on {1}", version.FullSemVer, version.CommitDate);
    Information("Commit hash: {0}", version.Sha);
});

Teardown(ctx =>
{
    // Executed AFTER the last task.
    Information("Finished running tasks.");
});

///////////////////////////////////////////////////////////////////////////////
// HELPERS
///////////////////////////////////////////////////////////////////////////////

void FixNUnitLoggerPaths()
{
    Information("Fixing NUnitXML.TestLogger paths");
    var files = GetFiles("/home/travis/.nuget/packages/nunitxml.testlogger/*/build/_common/*.dll");

    foreach (var file in files)
    {
        if (file.GetFilenameWithoutExtension().ToString().Contains("Nunit"))
        {
            var full = file.ToString();
            var fix = full.Replace("Nunit", "NUnit");
            Information("{0} --> {1}", full, fix);
            MoveFile(full, fix);
        }
    }
}

void Build(string project, string framework = null)
{
    var settings = new DotNetCoreBuildSettings
    {
        Configuration = configuration,
        NoRestore = true,
        ArgumentCustomization = args => args
            .Append($"/p:AssemblyVersion={version.AssemblySemVer}")
            .Append($"/p:NuGetVersion={version.NuGetVersionV2}")
    };

    if (!string.IsNullOrEmpty(framework))
        settings.Framework = framework;

    DotNetCoreBuild(project, settings);
}

///////////////////////////////////////////////////////////////////////////////
// TASKS
///////////////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
    {
        Information("Cleaning output directories");
        CleanDirectory("./artifacts");
        CleanDirectory("./publish");
        DotNetCoreClean(solution);
        CleanDirectory("./src/Colore.Tests/TestResults");
        CreateDirectory("./artifacts/nuget");
    });

Task("Restore")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        DotNetCoreRestore(solution);
    });

Task("Build")
    .IsDependentOn("Restore")
    .Does(() =>
    {
        if (isTravis)
            FixNUnitLoggerPaths();

        if (isWindows)
        {
            Build(solution);
        }
        else
        {
            foreach (var framework in frameworks)
            {
                Build(mainProject, framework);
            }

            Build(testProject);
        }
    });

Task("Test")
    .IsDependentOn("Build")
    .Does(() =>
    {
        var filters = ReadCoverageFilters("./tests/coverage-filters.txt");

        var settings = new DotNetCoreTestSettings
        {
            Configuration = configuration,
            NoBuild = true,
            NoRestore = true,
            Settings = "tests/coverlet.runsettings",
            ArgumentCustomization = args => args.Append("--collect:\"XPlat Code Coverage\"")
        };

        if (AppVeyor.IsRunningOnAppVeyor)
        {
            settings.Loggers = new[] { "AppVeyor" };
        }
        else
        {
            settings.Loggers = new[] { "nunit" };
        }

        DotNetCoreTest(testProject, settings);

        var testResults = GetFiles("tests/Colore.Tests/TestResults/*/coverage.cobertura.xml");
        CopyFiles(testResults, "./artifacts");
        MoveFile("./artifacts/coverage.cobertura.xml", "./artifacts/coverage.xml");
    });

Task("CoverageReport")
    .IsDependentOn("Test")
    .Does(() =>
    {
        ReportGenerator((FilePath)"./artifacts/coverage.xml", "./artifacts/coverage-report");
        Zip("./artifacts/coverage-report", $"./artifacts/colore_{version.SemVer}_coverage.zip");
    });

Task("Dist")
    .IsDependentOn("Test")
    .Does(() =>
    {
        foreach (var framework in frameworks)
        {
            var dir = $"./src/Colore/bin/{configuration}/{framework}/";
            var target = $"./artifacts/colore_{version.SemVer}_{framework}_anycpu.zip";
            Information("Zipping {0} to {1}", dir, target);
            Zip(dir, target, $"{dir}**/*.*");
        }
    });

Task("Publish")
    .IsDependentOn("Test")
    .Does(() =>
    {
        foreach (var framework in frameworks)
        {
            var settings = new DotNetCorePublishSettings
            {
                Framework = framework,
                Configuration = configuration,
                OutputDirectory = $"./publish/{framework}/",
                ArgumentCustomization = args => args
                    .Append($"/p:AssemblyVersion={version.AssemblySemVer}")
                    .Append($"/p:NuGetVersion={version.NuGetVersionV2}")
            };

            DotNetCorePublish("src/Colore", settings);

            var dir = $"./publish/{framework}/";
            var target = $"./artifacts/colore_{version.SemVer}_{framework}_full.zip";
            Information("Zipping {0} to {1}", dir, target);
            Zip(dir, target, $"{dir}**/*.*");
        }
    });

Task("Pack")
    .IsDependentOn("Test")
    .Does(() =>
    {
        MoveFiles(GetFiles("./src/**/*.nupkg"), "./artifacts/nuget");
        MoveFiles(GetFiles("./src/**/*.snupkg"), "./artifacts/nuget");
    });

Task("Docs")
    .IsDependentOn("Build")
    .Does(() =>
    {
        DocFxMetadata("./docs/docfx.json");
        if (isTravis)
        {
            DocFxBuild("./docs/docfx.json", new DocFxBuildSettings
            {
                ToolPath = "./docfx/docfx.exe"
            });
        }
        else
        {
            DocFxBuild("./docs/docfx.json");
        }

        Zip("./docs/_site", $"./artifacts/colore_{version.SemVer}_docs.zip");
    });

Task("Codecov")
    .WithCriteria(cover)
    .WithCriteria(isAppVeyor)
    .IsDependentOn("Test")
    .Does(() =>
    {
        var ccVersion = $"{version.FullSemVer}.build.{BuildSystem.AppVeyor.Environment.Build.Number}";
        var codecovPath = Context.Tools.Resolve("codecov.exe");

        Information("Running Codecov tool with version {0} on coverage result", ccVersion);

        Codecov(new CodecovSettings
        {
            Files = new[] { "./artifacts/coverage.xml" },
            Required = true,
            ////Branch = version.BranchName,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["APPVEYOR_BUILD_VERSION"] = ccVersion
            },
            Verbose = true,
            ToolPath = codecovPath
        });
    });

Task("CI")
    .IsDependentOn("Dist")
    .IsDependentOn("Publish")
    .IsDependentOn("Pack")
    .IsDependentOn("Docs")
    .IsDependentOn("CoverageReport")
    .IsDependentOn("Codecov");

Task("Travis").IsDependentOn("Test");

Task("AppVeyor").IsDependentOn("CI");

Task("GitHub")
    .IsDependentOn("Dist")
    .IsDependentOn("Publish")
    .IsDependentOn("Pack")
    ////.IsDependentOn("Docs")
    .IsDependentOn("CoverageReport")
    .IsDependentOn("Codecov");

Task("Default").IsDependentOn("Test");

RunTarget(target);

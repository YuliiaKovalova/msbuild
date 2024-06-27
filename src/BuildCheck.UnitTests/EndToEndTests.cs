// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Xml;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Microsoft.Build.UnitTests.Shared;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Build.BuildCheck.UnitTests;

public class EndToEndTests
{
    private readonly ITestOutputHelper _testOutput;

    public EndToEndTests(ITestOutputHelper output) => _testOutput = output;

    private static string AssemblyLocation { get; } = Path.Combine(Path.GetDirectoryName(typeof(EndToEndTests).Assembly.Location) ?? AppContext.BaseDirectory);

    private static string TestAssetsRootPath { get; } = Path.Combine(AssemblyLocation, "TestAssets");

    [Theory]
    [InlineData(false, true)]
    public void SampleAnalyzerIntegrationTest(bool buildInOutOfProcessNode, bool analysisRequested)
    {
        using (var env = TestEnvironment.Create(_testOutput))
        {
            // this is needed to ensure the binary logger does not pollute the environment
            _ = env.WithEnvironmentInvariant();
            _ = env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", buildInOutOfProcessNode ? "1" : "0");
            _ = env.SetEnvironmentVariable("MSBUILDLOGPROPERTIESANDITEMSAFTEREVALUATION", "1");

            string contents = File.ReadAllText(Path.Combine(TestAssetsRootPath, nameof(SampleAnalyzerIntegrationTest), "Project1.csproj"));
            string contents2 = File.ReadAllText(Path.Combine(TestAssetsRootPath, nameof(SampleAnalyzerIntegrationTest), "Project2.csproj"));

            TransientTestFolder workFolder = env.CreateFolder(createFolder: true);
            TransientTestFile projectFile = env.CreateFile(workFolder, "FooBar.csproj", contents);
            TransientTestFile projectFile2 = env.CreateFile(workFolder, "FooBar-Copy.csproj", contents2);

            // OSX links /var into /private, which makes Path.GetTempPath() return "/var..." but Directory.GetCurrentDirectory return "/private/var...".
            // This discrepancy breaks path equality checks in analyzers if we pass to MSBuild full path to the initial project.
            // See if there is a way of fixing it in the engine - tracked: https://github.com/orgs/dotnet/projects/373/views/1?pane=issue&itemId=55702688.
            env.SetCurrentDirectory(Path.GetDirectoryName(projectFile.Path));

            TransientTestFile config = env.CreateFile(
                workFolder,
                ".editorconfig",
                File.ReadAllText(Path.Combine(TestAssetsRootPath, nameof(SampleAnalyzerIntegrationTest), ".editorconfig")));


            string output = RunnerUtilities.ExecBootstrapedMSBuild(
                $"{Path.GetFileName(projectFile.Path)} /m:1 -nr:False -restore" +
                (analysisRequested ? " -analyze" : string.Empty), out bool success, false, env.Output, timeoutMilliseconds: 12000000);
            env.Output.WriteLine(output);
            success.ShouldBeTrue();
            // The conflicting outputs warning appears - but only if analysis was requested
            if (analysisRequested)
            {
                output.ShouldContain("BC0101");
            }
            else
            {
                output.ShouldNotContain("BC0101");
            }
        }
    }

    [Theory]
    [InlineData("AnalysisCandidate", new[] { "CustomRule1", "CustomRule2" })]
    [InlineData("AnalysisCandidateWithMultipleAnalyzersInjected", new[] { "CustomRule1", "CustomRule2", "CustomRule3" }, true)]
    public void CustomAnalyzerTest(string analysisCandidate, string[] expectedRegisteredRules, bool expectedRejectedAnalyzers = false)
    {
        using (var env = TestEnvironment.Create())
        {
            var analysisCandidatePath = Path.Combine(TestAssetsRootPath, analysisCandidate);
            AddCustomDataSourceToNugetConfig(analysisCandidatePath);

            string projectAnalysisBuildLog = RunnerUtilities.ExecBootstrapedMSBuild(
                $"{Path.Combine(analysisCandidatePath, $"{analysisCandidate}.csproj")} /m:1 -nr:False -restore /p:OutputPath={env.CreateFolder().Path} -analyze -verbosity:n",
                out bool successBuild);
            successBuild.ShouldBeTrue(projectAnalysisBuildLog);

            foreach (string registeredRule in expectedRegisteredRules)
            {
                projectAnalysisBuildLog.ShouldContain(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("CustomAnalyzerSuccessfulAcquisition", registeredRule));
            }

            if (expectedRejectedAnalyzers)
            {
                projectAnalysisBuildLog.ShouldContain(ResourceUtilities.FormatResourceStringStripCodeAndKeyword("CustomAnalyzerBaseTypeNotAssignable", "InvalidAnalyzer", "InvalidCustomAnalyzer, Version=15.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"));
            }
        }
    }

    private void AddCustomDataSourceToNugetConfig(string analysisCandidatePath)
    {
        var nugetTemplatePath = Path.Combine(analysisCandidatePath, "nugetTemplate.config");

        var doc = new XmlDocument();
        doc.LoadXml(nugetTemplatePath);
        if (doc.DocumentElement != null)
        {
            XmlNode? packageSourcesNode = doc.SelectSingleNode("//packageSources");

            // The test packages are generated during the test project build and saved in CustomAnalyzers folder.
            string analyzersPackagesPath = Path.Combine(Directory.GetParent(AssemblyLocation)?.Parent?.FullName ?? string.Empty, "CustomAnalyzers");
            AddPackageSource(doc, packageSourcesNode, "Key", analyzersPackagesPath);

            doc.Save(Path.Combine(analysisCandidatePath, "nuget.config"));
        }
    }

    private void AddPackageSource(XmlDocument doc, XmlNode? packageSourcesNode, string key, string value)
    {
        if (packageSourcesNode != null)
        {
            XmlElement addNode = doc.CreateElement("add");

            PopulateXmlAttribute(doc, addNode, "key", key);
            PopulateXmlAttribute(doc, addNode, "value", value);

            packageSourcesNode.AppendChild(addNode);
        }
    }

    private void PopulateXmlAttribute(XmlDocument doc, XmlNode node, string attributeName, string attributeValue)
    {
        node.ShouldNotBeNull($"The attribute {attributeName} can not be populated with {attributeValue}. Xml node is null.");
        var attribute = doc.CreateAttribute(attributeName);
        attribute.Value = attributeValue;
        node.Attributes!.Append(attribute);
    }
}

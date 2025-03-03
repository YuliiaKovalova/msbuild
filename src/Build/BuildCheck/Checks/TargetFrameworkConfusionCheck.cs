// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Collections;
using Microsoft.Build.Construction;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.BuildCheck.Checks;
internal class TargetFrameworkConfusionCheck : Check
{
    private const string RuleId = "BC0107";
    public static CheckRule SupportedRule = new CheckRule(RuleId, "TargetFrameworkConfusion",
        ResourceUtilities.GetResourceString("BuildCheck_BC0107_Title")!,
        ResourceUtilities.GetResourceString("BuildCheck_BC0107_MessageFmt")!,
        new CheckConfiguration() { RuleId = RuleId, Severity = CheckResultSeverity.Warning });

    public override string FriendlyName => "MSBuild.TargetFrameworkConfusion";

    public override IReadOnlyList<CheckRule> SupportedRules { get; } = [SupportedRule];

    public override void Initialize(ConfigurationContext configurationContext)
    {
        /* This is it - no custom configuration */
    }

    public override void RegisterActions(IBuildCheckRegistrationContext registrationContext)
    {
        registrationContext.RegisterEvaluatedPropertiesAction(EvaluatedPropertiesAction);
    }

    internal override bool IsBuiltIn => true;

    private readonly HashSet<string> _projectsSeen = new(MSBuildNameIgnoreCaseComparer.Default);

    private void EvaluatedPropertiesAction(BuildCheckDataContext<EvaluatedPropertiesCheckData> context)
    {
        // We want to avoid repeated checking of a same project (as it might be evaluated multiple times)
        //  for this reason we use a hashset with already seen projects.
        if (!_projectsSeen.Add(context.Data.ProjectFilePath))
        {
            return;
        }

        string? frameworks;
        string? framework;
        if (context.Data.EvaluatedProperties.TryGetValue(PropertyNames.TargetFrameworks, out frameworks)
            && context.Data.EvaluatedProperties.TryGetValue(PropertyNames.TargetFramework, out framework)
            && !context.Data.GlobalProperties.ContainsKey(PropertyNames.TargetFramework))
        {
            context.Data.EvaluatedPropertyToLocationMap.TryGetValue(PropertyNames.TargetFrameworks, out var propToLocations);
            var location = propToLocations?.FirstOrDefault().Item2;

            // {0} specifies 'TargetFrameworks' property '{1}' and 'TargetFramework' property '{2}'
            context.ReportResult(BuildCheckResult.Create(
                SupportedRule,
                ElementLocation.Create(location!.File, location.Line, location.Column),
                Path.GetFileName(context.Data.ProjectFilePath),
                frameworks,
                framework));
        }
    }
}

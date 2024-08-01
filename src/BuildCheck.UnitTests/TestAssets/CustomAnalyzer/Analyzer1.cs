﻿using System.Collections.Generic;
using Microsoft.Build.Construction;
using Microsoft.Build.Experimental.BuildCheck;

namespace CustomAnalyzer
{
    public sealed class Analyzer1 : BuildAnalyzer
    {
        public static BuildAnalyzerRule SupportedRule = new BuildAnalyzerRule(
            "X01234",
            "Title",
            "Description",
            "Message format: {0}",
            new BuildAnalyzerConfiguration() { Severity = BuildAnalyzerResultSeverity.Warning });

        public override string FriendlyName => "CustomRule1";

        public override IReadOnlyList<BuildAnalyzerRule> SupportedRules { get; } = new List<BuildAnalyzerRule>() { SupportedRule };

        public override void Initialize(ConfigurationContext configurationContext)
        {
            // configurationContext to be used only if analyzer needs external configuration data.
        }

        public override void RegisterActions(IBuildCheckRegistrationContext registrationContext)
        {
            registrationContext.RegisterEvaluatedPropertiesAction(EvaluatedPropertiesAction);
        }

        private void EvaluatedPropertiesAction(BuildCheckDataContext<EvaluatedPropertiesAnalysisData> context)
        {
            context.ReportResult(BuildCheckResult.Create(
                SupportedRule,
                ElementLocation.EmptyLocation,
                "Argument for the message format"));
        }
    }
}

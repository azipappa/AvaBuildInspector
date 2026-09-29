using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase.Validation.Performance;

namespace AvaBuildInspector
{
    internal enum TargetPlatform
    {
        PC,
        Android
    }

    internal enum AnalysisAccuracy
    {
        Exact,
        Estimated,
        Unavailable
    }

    internal sealed class Contributor
    {
        public string Name;
        public string Group;
        public double Value;
        public string Unit;
        public UnityEngine.Object Target;
        public UnityEngine.Object SecondaryTarget;
        public string Detail;
        public bool IsUnusedPhysBone;
    }

    internal sealed class MetricResult
    {
        public AvatarPerformanceCategory Category;
        public string Section;
        public string DisplayName;
        public string DisplayValue;
        public double? NumericValue;
        public string Unit;
        public PerformanceRating Rank;
        public AnalysisAccuracy Accuracy;
        public AnalysisAccuracy BreakdownAccuracy;
        public string Explanation;
        public readonly List<Contributor> Contributors = new List<Contributor>();
    }

    internal sealed class AvatarReport
    {
        public GameObject AvatarRoot;
        public TargetPlatform Platform;
        public DateTime Timestamp;
        public PerformanceRating OverallRank;
        public readonly List<MetricResult> Metrics = new List<MetricResult>();
        public readonly List<string> Warnings = new List<string>();

        public MetricResult Find(AvatarPerformanceCategory category)
        {
            return Metrics.Find(metric => metric.Category == category);
        }
    }
}

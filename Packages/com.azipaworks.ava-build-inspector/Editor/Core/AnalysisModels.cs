using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>SecondaryTargetの代わりに一括選択する対象（Materialを参照する全Renderer等）。</summary>
        public UnityEngine.Object[] RelatedTargets;
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
        public readonly List<Contributor> Materials = new List<Contributor>();
        public readonly List<string> Warnings = new List<string>();

        public MetricResult Find(AvatarPerformanceCategory category)
        {
            return Metrics.Find(metric => metric.Category == category);
        }
    }

    internal sealed class AvatarAnalysisStage
    {
        public string Label;
        public Action Execute;
    }

    internal sealed class AvatarAnalysisOperation
    {
        private readonly IReadOnlyList<AvatarAnalysisStage> _stages;
        private int _nextStage;

        public AvatarReport Report { get; set; }
        public int StageCount => _stages.Count;
        public int CompletedStageCount => _nextStage;
        public string CurrentStage => _nextStage < _stages.Count ? _stages[_nextStage].Label : "完了";
        public float Progress => _stages.Count == 0 ? 1f : (float)_nextStage / _stages.Count;

        public AvatarAnalysisOperation(IEnumerable<AvatarAnalysisStage> stages)
        {
            _stages = stages.ToArray();
        }

        public bool Advance()
        {
            if (_nextStage >= _stages.Count) return false;
            _stages[_nextStage].Execute();
            _nextStage++;
            return _nextStage < _stages.Count;
        }
    }
}

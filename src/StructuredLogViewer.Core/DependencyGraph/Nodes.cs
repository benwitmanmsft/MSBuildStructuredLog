using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public abstract class BaseNode
    {
        public HashSet<MSBuildStartNode> DiscoveredBy = new();

        public abstract ProjectEvaluationNode TheEvaluation { get; }

        public abstract IEnumerable<BaseNode> GetDependencies();

        public abstract TimeSpan GetDuration();

        public abstract TimeSpan GetTaskDuration();

        public abstract DateTime GetEnd();

        public abstract string IdString();

        public abstract string ToPrettyString();

        public virtual bool IsSentinel => false;

        public void Write(TextWriter writer)
        {
            writer.WriteLine($"{IdString()}: {ToPrettyString()}");
            writer.WriteLine($"Dependencies: {string.Join(", ", GetDependencies().Select(t => t.IdString()))}");
            writer.WriteLine($"DiscoveredBy: {string.Join(", ", DiscoveredBy.Select(t => t.IdString()))}");
            if (this is MSBuildStartNode start)
            {
                writer.WriteLine($"Discovers: {string.Join(", ", start.Discovered.Select(t => t.IdString()))}");
            }
        }
    }

    public class EmptyProjectBuildNode : BaseNode
    {
        public Project Project;
        public ProjectEvaluationNode EvaluationNode;

        public override ProjectEvaluationNode TheEvaluation => EvaluationNode;

        public override IEnumerable<BaseNode> GetDependencies() => [EvaluationNode];

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override TimeSpan GetTaskDuration() => Project.Duration;

        public override DateTime GetEnd() => Project.EndTime;

        public override string IdString() => $"Empty:{EvaluationNode.IdString()}";

        public override string ToString() => $"No Targets Built   Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}:";

        public override string ToPrettyString() => $"{EvaluationNode.ToPrettyString()}: No Targets Built";
    }

    public abstract class TargetBaseNode : BaseNode
    {
        public Target Target;
        public Project Project;
        public ProjectEvaluationNode EvaluationNode;
        public TargetBaseNode PriorTargetNode;

        public bool IsStart = true;
        public bool IsEnd = true;

        public override ProjectEvaluationNode TheEvaluation => Target == null ? null : EvaluationNode;

        public override sealed IEnumerable<BaseNode> GetDependencies()
        {
            return GetBaseDependencies().Concat(GetSpecificDependencies());
        }

        protected IEnumerable<BaseNode> GetBaseDependencies()
        {
            if (PriorTargetNode != null)
            {
                yield return PriorTargetNode;
            }
            else if (EvaluationNode != null)
            {
                yield return EvaluationNode;
            }
        }

        protected abstract IEnumerable<BaseNode> GetSpecificDependencies();
    }

    public class TargetFromCacheNode : TargetBaseNode
    {
        public TargetBaseNode CachedTarget;

        public override string IdString() => $"FromCache:{Target.Index}";

        public override string ToString()
        {
            return $"Target From Cache  Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}: {Target.Name}";
        }

        public override string ToPrettyString() => $"{EvaluationNode.PrettyName}: {Target.Name} (From Cache)";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [CachedTarget];

        public override TimeSpan GetDuration() => Target.Duration;

        public override DateTime GetEnd() => Target.EndTime;

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;
    }

    public class TargetSkippedNode : TargetBaseNode
    {
        public TargetSkipReason SkipReason;

        public override string IdString() => $"Skipped:{Target.Index}";

        public override string ToString()
        {
            return $"Target Skipped     Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}: {Target.Name}: {SkipReason}";
        }

        public override string ToPrettyString() => $"{EvaluationNode.PrettyName}: Skipped {Target.Name}: {SkipReason}";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [];

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;

        public override DateTime GetEnd() => Target.EndTime;
    }

    public class TargetTaskNode : TargetBaseNode
    {
        public DateTime? OverrideEndTime;
        public List<Task> Tasks = new();
        public long? CopiedFiles;
        public int Index = 0;

        private string TaskNames => Tasks.Count == 0 ? "(no tasks)" : string.Join(", ", Tasks.Select(t => t.Name));

        private string DisplayString => TaskNames + (CopiedFiles.HasValue ? $" (copied {CopiedFiles} files)" : string.Empty);

        public override string IdString() => $"Target:{Target.Index}:Tasks:{Index}";

        public override string ToString()
        {
            return $"Target Exec Tasks  Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}: {Target.Name}: {DisplayString}";
        }

        public override string ToPrettyString() => $"{EvaluationNode.PrettyName}: {Target.Name}: {DisplayString}";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [];

        public override TimeSpan GetDuration()
        {
            if (OverrideEndTime.HasValue)
            {
                return TimeSpan.Zero;
            }

            DateTime start = IsStart ? Target.StartTime : PriorTargetNode.GetEnd();
            DateTime end = GetEnd();

            return end - start;
        }

        public override TimeSpan GetTaskDuration() => Tasks.Aggregate(TimeSpan.Zero, (ts, task) => ts + task.Duration);

        public override DateTime GetEnd() => OverrideEndTime ?? (IsEnd ? Target.EndTime : Tasks.Last().EndTime);
    }

    public class CallTargetStartNode : TargetBaseNode
    {
        public CallTargetTask Task;
        public int Index = 0;

        public override string IdString() => $"Target:{Target.Index}:CallTargetStart:{Index}";

        public override string ToString()
        {
            return $"CallTarget Start   Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}: {Target.Name}: CallTarget #{Index}";
        }

        public override string ToPrettyString() => $"{EvaluationNode.PrettyName}: {Target.Name}: CallTarget Start #{Index}";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [];

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override DateTime GetEnd() => Task.StartTime;

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;
    }

    public class CallTargetEndNode : TargetBaseNode
    {
        public CallTargetTask Task;
        public int Index = 0;
        public TargetBaseNode CalledTarget;

        public override string IdString() => $"Target:{Target.Index}:CallTargetEnd:{Index}";

        public override string ToString()
        {
            return $"CallTarget End     Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}: {Target.Name}: CallTarget #{Index}";
        }

        public override string ToPrettyString() => $"{EvaluationNode.PrettyName}: {Target.Name}: CallTarget End #{Index}";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [CalledTarget];

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override DateTime GetEnd() => Task.EndTime;

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;
    }

    public class MSBuildStartNode : TargetBaseNode
    {
        public DateTime? CustomStartTime;
        public MSBuildTask Task;
        public int Index;
        public HashSet<BaseNode> Discovered = new();
        public TimeSpan EmptyDuration;

        public override bool IsSentinel => Target ==null;

        public override string IdString() => IsSentinel ? $"SentinelStart" : $"Target:{Target.Index}:MSBuildStart:{Index}";

        public override string ToString()
        {
            return Target == null
                ? $"Sentinel Start"
                : $"MSBuild Call Start Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}:{Target.Name}: #{Index} {Task.SourceFilePath}:{Task.LineNumber}";
        }

        public override string ToPrettyString() => IsSentinel ? "Start" : $"{EvaluationNode.PrettyName}: {Target.Name}: MSBuild Start #{Index}";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [];

        public override TimeSpan GetDuration() => EmptyDuration;

        public override DateTime GetEnd() => CustomStartTime ?? Task.StartTime;

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;
    }

    public class MSBuildEndNode : TargetBaseNode
    {
        public DateTime? CustomEndTime;
        public MSBuildTask Task;
        public int Index;
        public List<BaseNode> ProjectLastTargetNodes;

        public override bool IsSentinel => Target == null;

        public override string IdString() => IsSentinel ? $"SentinelEnd" : $"Target:{Target.Index}:MSBuildEnd:{Index}";

        public override string ToString()
        {
            return Target == null
                ? $"Sentinel End"
                : $"MSBuild Call End   Build:{Project.Id:D4} Eval:{EvaluationNode.NameString} {Project.ProjectFile}:{Target.Name}: #{Index} {Task.SourceFilePath}:{Task.LineNumber}";
        }

        public override string ToPrettyString() => IsSentinel ? "End" : $"{EvaluationNode.PrettyName}: {Target.Name}: MSBuild End #{Index}";

        protected override IEnumerable<BaseNode> GetSpecificDependencies() => [.. ProjectLastTargetNodes];

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override DateTime GetEnd() => CustomEndTime ?? (IsEnd ? Target.EndTime : Task.EndTime);

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;
    }

    public abstract class ProjectEvaluationNode : BaseNode
    {
        public override ProjectEvaluationNode TheEvaluation => this;

        public abstract int NodeId { get; }

        public abstract string PrettyName { get; }

        public abstract string NameString { get; }
    }

    public class MetaProjEvaluationNode : ProjectEvaluationNode
    {
        public string MetaProjFileName = null;

        public override string PrettyName => Path.GetFileName(MetaProjFileName);

        public override string NameString => $"Meta";

        public override string IdString() => $"MetaProj:{MetaProjFileName}";

        public override string ToString() => $"Evaluation                    Eval:{NameString} {MetaProjFileName}";

        public override string ToPrettyString() => $"{PrettyName}: Evaluation";

        public override IEnumerable<BaseNode> GetDependencies() => [];

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override DateTime GetEnd() => DiscoveredBy.First().GetEnd();

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;

        public override int NodeId => -1;

    }

    public class RealProjectEvaluationNode : ProjectEvaluationNode
    {
        public ProjectEvaluation Evaluation;
        public Dictionary<string, string> UniqueGlobalProperties;

        public override ProjectEvaluationNode TheEvaluation => this;

        public string UniqueGlobalPropertiesString => UniqueGlobalProperties.Count == 0 ?
            string.Empty :
            $" ({string.Join(", ", UniqueGlobalProperties.OrderBy(t => t.Key).Select(kv => $"{kv.Key}={kv.Value}"))})";

        public override string IdString() => $"Evaluation:{Evaluation.Id}";

        public override string PrettyName => $"{Path.GetFileName(Evaluation.ProjectFile)}{UniqueGlobalPropertiesString}";

        public override string NameString => $"{Evaluation.Id}";

        public override string ToString() =>
            $"Evaluation                    Eval:{Evaluation.Id:D4} {Evaluation.SourceFilePath} {UniqueGlobalPropertiesString}";

        public override string ToPrettyString() => $"{PrettyName}: Evaluation";

        public override IEnumerable<BaseNode> GetDependencies() => [];

        public override TimeSpan GetDuration() => Evaluation.Duration;

        public override DateTime GetEnd() => Evaluation.EndTime;

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;

        public override int NodeId => Evaluation.NodeId;
    }

    public class TargetFromResultsCache : BaseNode
    {
        public MSBuildStartNode RequestingNode;
        public Project Project;
        public string TargetName;

        public override ProjectEvaluationNode TheEvaluation => RequestingNode.EvaluationNode;

        public override string IdString() => $"FromResultsCache:{RequestingNode.EvaluationNode.IdString()}:{TargetName}";

        public override string ToString()
        {
            return $"From Results Cache Build:{Project.Id:D4} {Project.ProjectFile}:{TargetName}";
        }

        public override string ToPrettyString() => $"{Path.GetFileName(Project.ProjectFile)}: {TargetName}: From Results Cache";

        public override TimeSpan GetDuration() => TimeSpan.Zero;

        public override DateTime GetEnd() => RequestingNode.GetEnd();

        public override TimeSpan GetTaskDuration() => TimeSpan.Zero;

        public override IEnumerable<BaseNode> GetDependencies() => [];
    }
}

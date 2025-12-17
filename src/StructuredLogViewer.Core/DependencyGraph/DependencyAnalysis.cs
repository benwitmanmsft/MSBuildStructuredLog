using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;

namespace StructuredLogViewer.DependencyGraph
{
    public class DependencyAnalysis
    {
        public Graph Graph;

        public Dictionary<BaseNode, GraphWalk> NodeToWalk = new();

        private Action<GraphWalk> OnReady;

        private void CheckReady(GraphWalk walk)
        {
            if (walk.DependenciesRemaining == 0 && walk.Discovered)
            {
                Contract.Assert(!walk.Readied);
                OnReady(walk);
                walk.Readied = true;
            }
        }

        public void SatisfyDependents(GraphWalk walk)
        {
            foreach (var dependent in walk.Dependents)
            {
                --dependent.DependenciesRemaining;

                CheckReady(dependent);
            }
        }

        public void MakeDiscoveries(GraphWalk walk)
        {
            if (walk.Node is MSBuildStartNode startNode)
            {
                foreach (var discovered in startNode.Discovered)
                {
                    var discoveredWalk = NodeToWalk[discovered];

                    if (discoveredWalk.Discovered)
                    {
                        continue;
                    }

                    discoveredWalk.Discovered = true;
                    discoveredWalk.DiscoveredTime = walk.CriticalPathTime.Value;
                    discoveredWalk.DiscoveredBy = walk;

                    CheckReady(discoveredWalk);
                }
            }
        }

        public DependencyAnalysis(Graph graph, Action<GraphWalk> onReady)
        {
            Graph = graph;
            OnReady = onReady;

            int id = 0;
            NodeToWalk = graph.Nodes.ToDictionary(node => node, node => new GraphWalk()
            {
                Id = ++id,
                Node = node,
                Dependents = new(),
                DependenciesRemaining = node.GetDependencies().Count(),
                Discovered = !node.DiscoveredBy.Any()
            });

            foreach (var walk in NodeToWalk.Values)
            {
                foreach (var dependency in walk.Node.GetDependencies())
                {
                    NodeToWalk[dependency].Dependents.Add(walk);
                }
            }

            foreach (var walk in NodeToWalk.Values)
            {
                CheckReady(walk);
            }
        }

        private const string AbbreviatedTimeSpanFormat = @"mm\:ss\.fff";

        public static string TimeSpanString(TimeSpan ts)
        {
            char prefix = ts.TotalMilliseconds switch { 0 => ' ', < 0 => '-', > 0 => '+', _ => '?' };

            return $"{prefix}{ts.ToString(AbbreviatedTimeSpanFormat)}";
        }

        public static void PrintCriticalPath(GraphWalk endWalk, string baselineTitle, string bestTitle, StringBuilder criticalPathString, StringBuilder criticalPathAbbreviated, TimeSpan threshold)
        {
            criticalPathAbbreviated.AppendLine(@$"{baselineTitle,-10} +delta     ({bestTitle,-10} +delta    ) [loss time  +delta    ] [task time  +delta    ] Description");

            var list = endWalk.Enumerate().Reverse().ToList();

            GraphWalk groupPreviousEdge = null;
            List<GraphWalk> groupContents = null;

            GraphWalk lastNodePrinted = null;

            void PrintElement(
                TimeSpan pathStart, TimeSpan pathEnd,
                TimeSpan realStart, TimeSpan realEnd,
                TimeSpan taskStart, TimeSpan taskEnd,
                string label, string labelAbbv,
                bool indent, bool? isDiscovery)
            {
                var pathDelta = pathEnd - pathStart;
                var realDelta = realEnd - realStart;
                var taskDelta = taskEnd - taskStart;
                var comparison = realEnd - pathEnd;
                var comparisonDelta = realDelta - pathDelta;

                var stringIndent = indent ? new string(' ', 4) : string.Empty;
                var stringDiscovery = isDiscovery.HasValue ? (isDiscovery.Value ? "[F]" : "[D]") : "[ ]";

                criticalPathString.AppendLine($"{stringIndent}={stringDiscovery}=> {pathEnd:G} +{pathDelta:G} {realEnd:G} +{realDelta:G} d:{comparisonDelta:G} {taskEnd:G} +{taskDelta:G} {label}");
                criticalPathAbbreviated.AppendLine($"{stringIndent}{TimeSpanString(realEnd)} {TimeSpanString(realDelta)} ({TimeSpanString(pathEnd)} {TimeSpanString(pathDelta)}) [{TimeSpanString(comparison)} {TimeSpanString(comparisonDelta)}] [{TimeSpanString(taskEnd)} {TimeSpanString(taskDelta)}] {labelAbbv}");
            }

            TimeSpan totalTaskDuration = TimeSpan.Zero;

            void PrintNode(GraphWalk walk, bool indent, bool? isDiscovery)
            {
                var node = walk.Node;
                var pathStart = lastNodePrinted?.CriticalPathTime ?? TimeSpan.Zero;
                var taskStart = totalTaskDuration;
                totalTaskDuration += node.GetTaskDuration();
                PrintElement(pathStart, walk.CriticalPathTime.Value, lastNodePrinted?.BaselineTime.Value ?? TimeSpan.Zero, walk.BaselineTime.Value, taskStart, totalTaskDuration, node.ToString(), node.ToPrettyString(), indent, isDiscovery);
                lastNodePrinted = walk;
            }

            TimeSpan groupPreviousTaskDuration = TimeSpan.Zero;

            void FlushGroup()
            {
                if (groupContents == null)
                {
                    return;
                }

                if (groupContents.Count == 1)
                {
                    var walk = groupContents.Single();

                    PrintNode(walk, false, walk.CriticalPathIsDiscovery);
                }
                else
                {
                    var types = string.Join(", ", groupContents.Select(t => t.Node).GroupBy(t => t.GetType().Name).Select(t => (Name: t.Key, Count: t.Count())).OrderByDescending(t => t.Count).Select(t => $"{t.Name} ({t.Count})"));
                    var abbv = string.Join(", ",
                        groupContents
                        .Select(t => t.Node)
                        .GroupBy(t => t.TheEvaluation)
                        .Select(u =>
                        {
                            var executedStats = u
                                .OfType<TargetTaskNode>()
                                .GroupBy(t => t.Target)
                                .Select(t => (Targets: 1, Tasks: t.Sum(u => u.Tasks.Count)))
                                .Aggregate((Targets: 0, Tasks: 0), (a, b) => (a.Targets + b.Targets, a.Tasks + b.Tasks));

                            var cachedCount = u.OfType<TargetFromCacheNode>().Count();
                            var skippedCount = u.OfType<TargetSkippedNode>().Count();
                            List<string> cachedAndSkipped = new();

                            if (cachedCount > 0)
                            {
                                cachedAndSkipped.Add($"{cachedCount} Cached");
                            }

                            if (skippedCount > 0)
                            {
                                cachedAndSkipped.Add($"{skippedCount} Skipped");
                            }

                            return $"{u.Key.PrettyName}: {executedStats.Targets} Targets | {executedStats.Tasks} Tasks {(cachedAndSkipped.Count > 0 ? $" +({string.Join(", ", cachedAndSkipped)})" : "")}";
                        }));

                    var lastGroupMember = groupContents.Last();
                    var groupTaskDurationEnd = groupPreviousTaskDuration + groupContents.Aggregate(TimeSpan.Zero, (a, t) => a + t.Node.GetTaskDuration());

                    PrintElement(groupPreviousEdge.CriticalPathTime.Value, lastGroupMember.CriticalPathTime.Value, groupPreviousEdge.BaselineTime.Value, lastGroupMember.BaselineTime.Value, groupPreviousTaskDuration, groupTaskDurationEnd, $"Group: {types}", abbv, false, null);

                    foreach (var member in groupContents)
                    {
                        PrintNode(member, true, member.CriticalPathIsDiscovery);
                    }

                }

                groupPreviousEdge = null;
                groupContents = null;
            }

            GraphWalk lastWalk = null;
            foreach (var walk in list)
            {
                if (lastWalk == null)
                {
                    PrintNode(walk, false, false);
                }
                else
                {
                    if (lastWalk.Node.TheEvaluation != walk.Node.TheEvaluation)
                    {
                        FlushGroup();
                    }

                    if ((walk.CriticalPathTime.Value - lastWalk.CriticalPathTime.Value) < threshold)
                    {
                        if (groupContents == null)
                        {
                            groupPreviousEdge = lastWalk;
                            groupPreviousTaskDuration = totalTaskDuration;
                            groupContents = new();
                        }

                        groupContents.Add(walk);
                    }
                    else
                    {
                        FlushGroup();

                        PrintNode(walk, false, walk.CriticalPathIsDiscovery);
                    }
                }

                lastWalk = walk;
            }

            FlushGroup();
        }
    }
}

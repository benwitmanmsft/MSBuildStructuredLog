using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public static class TextualAnalyzer
    {
        public static void Run(Build build)
        {
            var graph = new Graph(build);
            graph.Write(graph.Build.LogFilePath + ".graph.txt");

            var graphStartAnalysis = new StartAnalysis(graph);
            var graphCriticalPathAnalysis = new CriticalPathAnalysis(graph);

            void Simulate(int count, Dictionary<RealProjectEvaluationNode, int> affinities, string fileName)
            {
                StringBuilder simulationSteps = new StringBuilder();
                var graphSimulationAnalysis = new SimulationAnalysis(
                    graph,
                    graphCriticalPathAnalysis,
                    count,
                    affinities,
                    c => simulationSteps.AppendLine($"{c.Time:G}: {c.StepType} {c.Walk.Node.ToPrettyString()} (Remaining: {c.RemainingCriticalPath:G}"));
                File.WriteAllText($"{build.LogFilePath}.simulation.{fileName}.steps.txt", simulationSteps.ToString());

                StringBuilder simulationPathString = new();
                StringBuilder simulationPathMarkdown = new();
                DependencyAnalysis.PrintCriticalPath(
                    graphSimulationAnalysis.SimulatedPath, "critical", "sim", simulationPathString, simulationPathMarkdown,
                    baselineThreshold: TimeSpan.FromMilliseconds(500), bestThreshold: TimeSpan.FromMilliseconds(500),
                    canGroup: true, canGroupEvaluations: true, canGroupCopies: true);

                StringBuilder simulationPathSummary = new();
                graphSimulationAnalysis.SimulatedPathStats.WriteSummaries(simulationPathSummary);

                File.WriteAllText($"{build.LogFilePath}.simulation.{fileName}.md", string.Join(Environment.NewLine,
                    $"# Simulation with {count} workers" + (affinities != null ? " with affinities" : ""),
                    "## Critical Path Summary",
                    simulationPathSummary.ToString(),
                    "## Critical Path",
                    simulationPathMarkdown.ToString(),
                    "## Raw Critical Path",
                    "```",
                    simulationPathString.ToString(),
                    "```"
                ));
            }

            Simulate(graphStartAnalysis.NodeEvaluations.Count * 1, affinities: null, "n");
            Simulate(graphStartAnalysis.NodeEvaluations.Count * 1, graphStartAnalysis.NodeEvaluations.SelectMany(worker => worker.Value.Select(t => new { WorkerId = worker.Key, Eval = t })).ToDictionary(t => t.Eval, t => t.WorkerId), "n.affinity");
            Simulate(graphStartAnalysis.NodeEvaluations.Count * 2, affinities: null, "2n");
            Simulate(graphStartAnalysis.NodeEvaluations.Count * 2, graphStartAnalysis.NodeEvaluations.SelectMany(worker => worker.Value.Select((t, i) => new { WorkerId = worker.Key + (graphStartAnalysis.NodeEvaluations.Count * (i % 2)), Eval = t })).ToDictionary(t => t.Eval, t => t.WorkerId), "2n.affinity");

            StringBuilder criticalPathString = new();
            StringBuilder criticalPathMarkdown = new();
            DependencyAnalysis.PrintCriticalPath(
                graphCriticalPathAnalysis.CriticalPath, "actual", "critical", criticalPathString, criticalPathMarkdown,
                    baselineThreshold: TimeSpan.FromMilliseconds(500), bestThreshold: TimeSpan.FromMilliseconds(500),
                    canGroup: true, canGroupEvaluations: true, canGroupCopies: true);

            var criticalPath = graphCriticalPathAnalysis.CriticalPath.Enumerate().ToList();
            criticalPath.Reverse();

            var nodeUsage = new NodeActivity(graph, criticalPath.First().Node.GetEnd());

            StringBuilder criticalPathRelevantNodeUtilizations = new();

            criticalPathRelevantNodeUtilizations.AppendLine("### Critical Path Gap Analysis");
            criticalPathRelevantNodeUtilizations.AppendLine(@"```");
            GraphWalk last = null;
            foreach (var walk in criticalPath)
            {
                if (last != null && !last.Node.IsSentinel && !walk.Node.IsSentinel)
                {
                    var realDelta = walk.BaselineTime.Value - last.BaselineTime.Value;
                    var critDelta = walk.CriticalPathTime.Value - last.CriticalPathTime.Value;
                    if (realDelta - critDelta > walk.Node.GetDuration() + TimeSpan.FromMilliseconds(500))
                    {
                        if (walk.Node is MSBuildEndNode endNode)
                        {
                            foreach (var dependency in endNode.ProjectLastTargetNodes)
                            {
                                if (dependency is TargetFromCacheNode)
                                {
                                    var start = last.BaselineTime.Value;
                                    var end = walk.BaselineTime.Value;
                                    criticalPathRelevantNodeUtilizations.AppendLine($"Analyzing FromCache delay {start:G} => {end:G} on node {dependency.TheEvaluation.NodeId} because {dependency.ToPrettyString()} needed to run there");
                                    foreach (var n in nodeUsage.Timelines[dependency.TheEvaluation.NodeId].Where(t => t.Start < end && t.End > start && t.First.TheEvaluation != dependency.TheEvaluation))
                                    {
                                        criticalPathRelevantNodeUtilizations.AppendLine($"  {n.Start:G} => {n.End:G}: {n.First.TheEvaluation.PrettyName}: {n.First.ToPrettyString()} => {n.Last.ToPrettyString()}");
                                    }
                                }
                            }
                        }
                    }
                }

                last = walk;
            }
            criticalPathRelevantNodeUtilizations.AppendLine(@"```");

            StringBuilder criticalPathSummary = new();
            criticalPathSummary.AppendLine();
            graphCriticalPathAnalysis.CriticalPathStats.WriteSummaries(criticalPathSummary);

            StringBuilder everythingSummary = new();
            graph.AggregateStats.WriteSummaries(everythingSummary);

            StringBuilder nodeSummary = new();
            graphStartAnalysis.PrintStartAnalysis(nodeSummary);

            File.WriteAllText($"{build.LogFilePath}.md", string.Join(Environment.NewLine,
                "# General Analysis",

                "## Everything Summary",
                everythingSummary.ToString(),
                "## Node Analysis Summary",
                nodeSummary.ToString()
            ));

            File.WriteAllText($"{build.LogFilePath}.criticalpath.md", string.Join(Environment.NewLine,
                "# Critical Path Analysis",

                "## Critical Path Summary",
                criticalPathSummary.ToString(),
                "## Critical Path",
                criticalPathMarkdown.ToString(),
                "## Critical Path Gap Analysis",
                criticalPathRelevantNodeUtilizations.ToString(),

                "## Raw Critical Path",
                "```",
                criticalPathString.ToString(),
                "```"
            ));

            File.WriteAllText($"{build.LogFilePath}.criticalpathtimes.txt", string.Join(Environment.NewLine,
                graphCriticalPathAnalysis.CriticalPaths.Values
                .OrderBy(t => t.CriticalPathTime.Value)
                .Select(t => $"{t.CriticalPathTime.Value:G} {t.Node.ToPrettyString()}")
            ));
        }

    }
}

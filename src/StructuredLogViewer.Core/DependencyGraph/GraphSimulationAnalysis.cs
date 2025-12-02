using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public class GraphSimulationAnalysis
    {
        public Graph Graph;
        public Dictionary<BaseNode, GraphWalk> SimulatedPaths;
        public GraphWalk SimulatedPath;

        private class DependentWalkNode
        {
            public GraphWalk Walk;
            public List<DependentWalkNode> Dependents = new();
            public List<DependentWalkNode> Dependencies = new();
            public int DependenciesRemaining;
            public TimeSpan? RemainingCriticalPath;

            public void AddDependency(DependentWalkNode dependency)
            {
                Dependencies.Add(dependency);
                dependency.Dependents.Add(this);
                ++DependenciesRemaining;
            }
        }

        public GraphSimulationAnalysis(Graph graph, GraphCriticalPathAnalysis criticalPathAnalysis, int workers, StringBuilder stepStringBuilder)
        {
            Graph = graph;

            Stack<DependentWalkNode> processingStack = new();

            void TryPush(DependentWalkNode node)
            {
                if (node.DependenciesRemaining == 0)
                {
                    processingStack.Push(node);
                }
            }

            void Satisfy(DependentWalkNode node)
            {
                foreach (var dependent in node.Dependents)
                {
                    --dependent.DependenciesRemaining;

                    TryPush(dependent);
                }
            }

            Dictionary<BaseNode, DependentWalkNode> nodeToDependentWalkNode = criticalPathAnalysis.CriticalPaths.Values.ToDictionary(
                walk => walk.Node,
                walk => new DependentWalkNode() { Walk = walk }
            );

            foreach (var dependentWalkNode in nodeToDependentWalkNode.Values)
            {
                foreach (var dependency in dependentWalkNode.Walk.Node.GetDependencies())
                {
                    nodeToDependentWalkNode[dependency].AddDependency(dependentWalkNode);
                }

                if (dependentWalkNode.Walk.DiscoveredBy != null)
                {
                    nodeToDependentWalkNode[dependentWalkNode.Walk.DiscoveredBy.Node].AddDependency(dependentWalkNode);
                }
            }

            foreach (var dependentWalkNode in nodeToDependentWalkNode.Values)
            {
                TryPush(dependentWalkNode);
            }

            while (processingStack.Count > 0)
            {
                var dependentWalkNode = processingStack.Pop();
                var walk = dependentWalkNode.Walk;

                TimeSpan maxRemainingCriticalPath = TimeSpan.Zero;
                foreach (var dependency in dependentWalkNode.Dependencies)
                {
                    var dependentRemainingCriticalPath = dependency.RemainingCriticalPath.Value;
                    maxRemainingCriticalPath = TimeSpan.FromTicks(Math.Max(maxRemainingCriticalPath.Ticks, dependentRemainingCriticalPath.Ticks));
                }

                dependentWalkNode.RemainingCriticalPath = maxRemainingCriticalPath + walk.Node.GetDuration();

                Satisfy(dependentWalkNode);
            }

            SortedSet<(TimeSpan RemainingCriticalPath, int Id, GraphWalk Walk)> ready = new();
            SortedSet<(TimeSpan SimulatedEndTime, int Id, GraphWalk Walk)> inProgress = new();

            TimeSpan currentTime = TimeSpan.Zero;

            var dependencyAnalysis = new GraphDependencyAnalysis(graph, walk =>
            {
                stepStringBuilder?.AppendLine($"{currentTime:G}: Ready: {walk.Node.ToPrettyString()} (Remaining Critical Path Time: {nodeToDependentWalkNode[walk.Node].RemainingCriticalPath.Value})");
                ready.Add((nodeToDependentWalkNode[walk.Node].RemainingCriticalPath.Value, walk.Id, walk));
            });

            while (ready.Any() || inProgress.Any())
            {
                while (inProgress.Count < workers && ready.Any())
                {
                    var next = ready.Max;
                    ready.Remove(next);
                    stepStringBuilder?.AppendLine($"{currentTime:G}: Start: {next.Walk.Node.ToPrettyString()}");
                    var criticalPathWalk = criticalPathAnalysis.CriticalPaths[next.Walk.Node].CriticalPath;
                    next.Walk.CriticalPath = criticalPathWalk == null ? null : dependencyAnalysis.NodeToWalk[criticalPathWalk.Node];
                    next.Walk.CriticalPathTime = currentTime + next.Walk.Node.GetDuration();
                    next.Walk.CriticalPathIsDiscovery = criticalPathAnalysis.CriticalPaths[next.Walk.Node].CriticalPathIsDiscovery;
                    next.Walk.BaselineTime = criticalPathAnalysis.CriticalPaths[next.Walk.Node].CriticalPathTime.Value;
                    inProgress.Add((currentTime + next.Walk.Node.GetDuration(), next.Id, next.Walk));
                }

                var finished = inProgress.Min;
                inProgress.Remove(finished);
                currentTime = finished.SimulatedEndTime;
                var walk = finished.Walk;

                stepStringBuilder?.AppendLine($"{currentTime:G}: Stop: {finished.Walk.Node.ToPrettyString()}");

                dependencyAnalysis.MakeDiscoveries(walk);
                dependencyAnalysis.SatisfyDependents(walk);
            }

            // File.WriteAllText($"{graph.Build.LogFilePath}.simulationsteps.txt", sb.ToString());

            SimulatedPaths = dependencyAnalysis.NodeToWalk;
            SimulatedPath = SimulatedPaths[graph.EndNode];

            Contract.Assert(SimulatedPaths.Values.All(t => t.CriticalPathTime.HasValue));
        }
    }

}

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;

namespace StructuredLogViewer.DependencyGraph
{
    public class SimulationAnalysis
    {
        public Graph Graph;
        public Dictionary<BaseNode, GraphWalk> SimulatedPaths;
        public GraphWalk SimulatedPath;
        public AggregateStats SimulatedPathStats = new();

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

        public enum StepType
        {
            Ready,
            Start,
            Stop,
        }

        public class SimulationStep
        {
            public StepType StepType;
            public TimeSpan Time;
            public GraphWalk Walk;
            public TimeSpan RemainingCriticalPath;
            public int? Worker;
        }

        public SimulationAnalysis(Graph graph, CriticalPathAnalysis criticalPathAnalysis, int workers, Dictionary<RealProjectEvaluationNode, int> affinity, Action<SimulationStep> stepCallback)
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

            int? GetAffinity(BaseNode node) => affinity != null && node.TheEvaluation is RealProjectEvaluationNode realNode ? affinity[realNode] : null;

            SortedSet<(TimeSpan RemainingCriticalPath, int Id, int? WorkerId, GraphWalk Walk)> ready = new();
            SortedSet<(TimeSpan SimulatedEndTime, int Id, int ? WorkerId, GraphWalk Walk)> inProgress = new();

            TimeSpan currentTime = TimeSpan.Zero;

            var dependencyAnalysis = new DependencyAnalysis(graph, walk =>
            {
                var workerId = GetAffinity(walk.Node);
                stepCallback?.Invoke(new SimulationStep() { StepType = StepType.Ready, Time = currentTime, Walk = walk, RemainingCriticalPath = nodeToDependentWalkNode[walk.Node].RemainingCriticalPath.Value, Worker = workerId });
                ready.Add((nodeToDependentWalkNode[walk.Node].RemainingCriticalPath.Value, walk.Id, workerId, walk));
            });

            HashSet<int> workersInUse = new();

            while (ready.Any() || inProgress.Any())
            {
                while (inProgress.Count < workers && ready.Reverse().FirstOrDefault(t => !(t.WorkerId.HasValue && workersInUse.Contains(t.WorkerId.Value))) is { Walk: { } } next)
                {
                    ready.Remove(next);
                    stepCallback?.Invoke(new SimulationStep() { StepType = StepType.Start, Time = currentTime, Walk = next.Walk, RemainingCriticalPath = nodeToDependentWalkNode[next.Walk.Node].RemainingCriticalPath.Value, Worker = next.WorkerId });
                    var criticalPathWalk = criticalPathAnalysis.CriticalPaths[next.Walk.Node].CriticalPath;
                    next.Walk.CriticalPath = criticalPathWalk == null ? null : dependencyAnalysis.NodeToWalk[criticalPathWalk.Node];
                    next.Walk.CriticalPathTime = currentTime + next.Walk.Node.GetDuration();
                    next.Walk.CriticalPathIsDiscovery = criticalPathAnalysis.CriticalPaths[next.Walk.Node].CriticalPathIsDiscovery;
                    next.Walk.BaselineTime = criticalPathAnalysis.CriticalPaths[next.Walk.Node].CriticalPathTime.Value;
                    inProgress.Add((currentTime + next.Walk.Node.GetDuration(), next.Id, next.WorkerId, next.Walk));

                    if (next.WorkerId.HasValue)
                    {
                        workersInUse.Add(next.WorkerId.Value);
                    }
                }

                var finished = inProgress.Min;
                inProgress.Remove(finished);
                currentTime = finished.SimulatedEndTime;
                var walk = finished.Walk;
                if (finished.WorkerId.HasValue)
                {
                    workersInUse.Remove(finished.WorkerId.Value);
                }

                stepCallback?.Invoke(new SimulationStep() { StepType = StepType.Stop, Time = currentTime, Walk = finished.Walk, RemainingCriticalPath = nodeToDependentWalkNode[finished.Walk.Node].RemainingCriticalPath.Value, Worker = finished.WorkerId });

                dependencyAnalysis.MakeDiscoveries(walk);
                dependencyAnalysis.SatisfyDependents(walk);
            }

            // File.WriteAllText($"{graph.Build.LogFilePath}.simulationsteps.txt", sb.ToString());

            SimulatedPaths = dependencyAnalysis.NodeToWalk;
            SimulatedPath = SimulatedPaths[graph.EndNode];

            foreach (var walk in SimulatedPath.Enumerate())
            {
                SimulatedPathStats.AddNode(walk.Node);
            }

            Contract.Assert(SimulatedPaths.Values.All(t => t.CriticalPathTime.HasValue));
        }
    }

}

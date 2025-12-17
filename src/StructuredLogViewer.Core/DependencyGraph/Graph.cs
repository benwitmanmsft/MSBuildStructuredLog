using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

namespace StructuredLogViewer.DependencyGraph
{
    public class Graph
    {
        public Build Build;
        public HashSet<BaseNode> Nodes = new();
        public Dictionary<int, ProjectEvaluationNode> EvaluationNodes = new();
        public MSBuildStartNode StartNode;
        public MSBuildEndNode EndNode;
        public AggregateStats AggregateStats = new();

        private List<(Target dependency, TargetFromCacheNode dependent)> AlreadyBuilt = new();
        private Dictionary<Target, TargetBaseNode> TargetToLastNode = new();

        private T ProcessCreatedNode<T>(T node) where T : BaseNode
        {
            Nodes.Add(node);

            if (node is ProjectEvaluationNode evaluationNode)
            {
                EvaluationNodes.Add(evaluationNode.Evaluation.Id, evaluationNode);
            }

            return node;
        }

        private BaseNode ProcessProject(Project project, MSBuildStartNode requestingNode)
        {
            T UpdateCreatedBaseNode<T>(T node) where T : BaseNode
            {
                ProcessCreatedNode(node);

                node.DiscoveredBy.Add(requestingNode);
                requestingNode.Discovered.Add(node);

                return node;
            }

            var evaluation = project.GetEvaluation(Build);
            if (evaluation == null)
            {
                foreach (var target in project.Children.OfType<Target>())
                {
                    if (target.OriginalNode != null)
                    {
                        evaluation = target.OriginalNode.GetNearestParent<Project>().GetEvaluation(Build);
                        break;
                    }
                }

                if (evaluation == null)
                {
                    return UpdateCreatedBaseNode(new TargetFromResultsCache()
                    {
                        Project = project,
                        RequestingNode = requestingNode,
                        TargetName = project.Children.OfType<Folder>().Single(t => t.Name == Strings.EntryTargets).Children.OfType<EntryTarget>().Select(t => t.Name).Single()
                    });
                }
            }

            var evaluationNode = EvaluationNodes[evaluation.Id];

            evaluationNode.DiscoveredBy.Add(requestingNode);
            requestingNode.Discovered.Add(evaluationNode);

            var targets = project.Children.OfType<Target>().ToList();

            // no target found to build, so it just noops
            if (targets.Count == 0)
            {
                return UpdateCreatedBaseNode(new EmptyProjectBuildNode()
                {
                    Project = project,
                    EvaluationNode = evaluationNode
                });
            }

            int nextTarget = 0;

            Target NextTarget(TargetBaseNode lastTargetEnd)
            {
                Contract.Assert(nextTarget < targets.Count);

                var target = targets[nextTarget++];

                Contract.Assert(project.StartTime <= target.StartTime && target.EndTime <= project.EndTime);

                // I've seen cases where targets with zero duration can be slightly before the prior target so we're only going to enforce on the propr non-zero duration target
                if (target.Duration > TimeSpan.Zero)
                {
                    var lastNonEmpty = lastTargetEnd;
                    while (lastNonEmpty != null && lastNonEmpty.Target.Duration == TimeSpan.Zero)
                    {
                        lastNonEmpty = lastNonEmpty.PriorTargetNode;
                    }

                    Contract.Assert(lastNonEmpty == null || target.StartTime >= lastNonEmpty.GetEnd());
                }

                return target;
            }

            bool IsNextTargetBefore(DateTime endTime)
            {
                if (nextTarget >= targets.Count)
                {
                    return false;
                }

                return targets[nextTarget].StartTime < endTime && targets[nextTarget].EndTime <= endTime;
            }

            bool IsNextTargetNamed(string name)
            {
                if (string.IsNullOrEmpty(name) || nextTarget >= targets.Count)
                {
                    return false;
                }

                return string.Equals(targets[nextTarget].Name, name, StringComparison.OrdinalIgnoreCase);
            }

            TargetBaseNode ProcessTarget(Target target, TargetBaseNode lastTargetBaseNode)
            {
                TargetBaseNode intraTargetLast = null;
                int msbuildIndex = 0;
                int callTargetIndex = 0;

                T UpdateCreatedTargetBaseNode<T>(T node) where T : TargetBaseNode
                {
                    UpdateCreatedBaseNode(node);

                    node.Target = target;
                    node.Project = project;
                    node.EvaluationNode = evaluationNode;
                    node.PriorTargetNode = intraTargetLast ?? lastTargetBaseNode;
                    node.IsStart = intraTargetLast == null;
                    node.IsEnd = true;

                    if (intraTargetLast != null)
                    {
                        intraTargetLast.IsEnd = false;
                    }

                    intraTargetLast = node;
                    return node;
                }

                if (target.OriginalNode is Target originalNode)
                {
                    AlreadyBuilt.Add((originalNode, UpdateCreatedTargetBaseNode(new TargetFromCacheNode())));
                }
                else if (target.TargetSkipReason is TargetSkipReason skippedReason)
                {
                    UpdateCreatedTargetBaseNode(new TargetSkippedNode()
                    {
                        SkipReason = skippedReason
                    });
                }
                else
                {
                    int currentTaskNodeIndex = 0;
                    TargetTaskNode currentTargetTaskNode = null;
                    var tasks = target.Children.OfType<Task>().ToArray();

                    if (tasks.Length > 0)
                    {
                        Contract.Assert(tasks.All(t => target.StartTime <= t.StartTime && t.EndTime <= target.EndTime));

                        for (int i = 0; i < tasks.Length; i++)
                        {
                            var task = tasks[i];

                            if (task is MSBuildTask msbuildTask)
                            {
                                currentTargetTaskNode = null;

                                var msbuildStartNode = UpdateCreatedTargetBaseNode(new MSBuildStartNode()
                                {
                                    Task = msbuildTask,
                                    Index = msbuildIndex
                                });

                                var msbuildEndNode = UpdateCreatedTargetBaseNode(new MSBuildEndNode()
                                {
                                    Task = msbuildTask,
                                    Index = msbuildIndex
                                });

                                // assumes parallel right now
                                msbuildEndNode.ProjectLastTargetNodes = msbuildTask.Children.OfType<Project>().Select(t => ProcessProject(t, msbuildStartNode)).ToList();

                                if (msbuildEndNode.ProjectLastTargetNodes.Count == 0)
                                {
                                    msbuildStartNode.EmptyDuration = msbuildEndNode.GetEnd() - msbuildStartNode.GetEnd();
                                }

                                ++msbuildIndex;
                            }
                            else if (task is CallTargetTask callTargetTask)
                            {
                                currentTargetTaskNode = null;

                                var callTargetStartNode = UpdateCreatedTargetBaseNode(new CallTargetStartNode()
                                {
                                    Task = callTargetTask,
                                    Index = callTargetIndex
                                });

                                TargetBaseNode lastCallTargetNode = callTargetStartNode;
                                var callTargetNames = new Queue<string>(callTargetTask.GetTargets());

                                // we need to make sure to get all batches, and they can be 0 duration and so not technically be before...
                                string lastCallTargetName = null;

                                while (callTargetNames.Count > 0 || IsNextTargetBefore(callTargetTask.EndTime) || IsNextTargetNamed(lastCallTargetName))
                                {
                                    var nextTarget = NextTarget(lastCallTargetNode.PriorTargetNode);
                                    lastCallTargetNode = ProcessTarget(nextTarget, lastCallTargetNode);

                                    if (callTargetNames.Count > 0 && string.Equals(callTargetNames.Peek(), nextTarget.Name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        lastCallTargetName = callTargetNames.Dequeue();
                                    }
                                }

                                var callTargetEndNode = UpdateCreatedTargetBaseNode(new CallTargetEndNode()
                                {
                                    Task = callTargetTask,
                                    Index = callTargetIndex,
                                    CalledTarget = lastCallTargetNode
                                });

                                ++callTargetIndex;
                            }
                            else
                            {
                                if (currentTargetTaskNode == null)
                                {
                                    currentTargetTaskNode = UpdateCreatedTargetBaseNode(new TargetTaskNode() {
                                        Index = currentTaskNodeIndex++
                                    });
                                }

                                currentTargetTaskNode.Tasks.Add(task);

                                switch (task.Name)
                                {
                                    case "Copy":
                                        currentTargetTaskNode.CopiedFiles ??= 0;
                                        currentTargetTaskNode.CopiedFiles += task.FindChildrenRecursive<Message>().Where(t => t.Text.StartsWith("Copying file from")).Count();
                                        break;
                                    case "Robocopy":
                                        currentTargetTaskNode.CopiedFiles ??= 0;
                                        currentTargetTaskNode.CopiedFiles += Math.Max(task.FindChildrenRecursive<Message>().Where(t => t.Text.StartsWith("Copied ")).Count() - 1, 0);
                                        break;
                                }
                            }
                        }
                    }
                    else
                    {
                        UpdateCreatedTargetBaseNode(new TargetTaskNode()
                        {
                            Index = currentTaskNodeIndex++
                        });
                    }
                }

                return TargetToLastNode[target] = intraTargetLast;
            }

            TargetBaseNode lastTargetNode = null;
            while (nextTarget < targets.Count)
            {
                var target = NextTarget(lastTargetNode);
                lastTargetNode = ProcessTarget(target, lastTargetNode);
            }

            Contract.Assert(lastTargetNode != null);

            return lastTargetNode;
        }

        public Graph(Build build)
        {
            Build = build;

            // Create Evaluation Nodes
            foreach (var evaluation in Build.EvaluationFolder.Children.OfType<ProjectEvaluation>())
            {
                var evaluationNode = ProcessCreatedNode(new ProjectEvaluationNode() { Evaluation = evaluation });
            }

            // Construct Build Graph
            StartNode = ProcessCreatedNode(new MSBuildStartNode() { CustomStartTime = EvaluationNodes.Values.Min(p => p.Evaluation.StartTime) });
            EndNode = ProcessCreatedNode(new MSBuildEndNode() { CustomEndTime = Build.Children.OfType<Project>().Max(p => p.EndTime), PriorTargetNode = StartNode });
            EndNode.ProjectLastTargetNodes = Build.Children.OfType<Project>().Select(p => ProcessProject(p, StartNode)).ToList();

            // Determine UniqueGlobalProperties
            foreach (var projectGroup in Nodes.OfType<ProjectEvaluationNode>().GroupBy(t => t.Evaluation.ProjectFile))
            {
                var common = projectGroup
                    .Select(t => t.Evaluation.GetGlobalProperties())
                    .Aggregate((a, b) => a.Intersect(b).ToDictionary(t => t.Key, t => t.Value));

                foreach (var projectEvaluationNode in projectGroup)
                {
                    projectEvaluationNode.UniqueGlobalProperties = projectEvaluationNode.Evaluation.GetGlobalProperties()
                        .Where(t => !common.ContainsKey(t.Key) || common[t.Key] != t.Value)
                        .ToDictionary(t => t.Key, t => t.Value);
                }
            }

            // Process Already Built Targets
            foreach (var already in AlreadyBuilt)
            {
                var (dependency, dependent) = already;
                dependent.CachedTarget = TargetToLastNode[dependency];
            }

            AlreadyBuilt.Clear();
            TargetToLastNode.Clear();

            foreach (var node in Nodes)
            {
                AggregateStats.AddNode(node);
            }
        }

        public void Write(string filename)
        {
            using (var writer = new StreamWriter(filename))
            {
                foreach (var node in Nodes.OrderBy(n => n.GetEnd()))
                {
                    node.Write(writer);
                    writer.WriteLine();
                }
            }
        }
    }

    public class GraphWalk
    {
        public int Id;
        public BaseNode Node;
        public List<GraphWalk> Dependents;
        public long DependenciesRemaining;
        public bool Discovered = false;
        public TimeSpan? DiscoveredTime = null;
        public GraphWalk DiscoveredBy;
        public TimeSpan? CriticalPathTime;
        public GraphWalk CriticalPath;
        public bool CriticalPathIsDiscovery;
        public bool Readied = false;
        public TimeSpan? BaselineTime;

        public IEnumerable<GraphWalk> Enumerate()
        {
            for (var walk = this; walk != null; walk = walk.CriticalPath)
            {
                yield return walk;
            }
        }
    }
}

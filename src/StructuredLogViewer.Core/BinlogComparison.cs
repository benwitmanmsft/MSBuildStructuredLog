using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.Msagl.Core.ProjectionSolver;
using static StructuredLogViewer.BinlogComparison.NormalizedBinlog;

namespace StructuredLogViewer
{
    public class BinlogComparison
    {

        public class NormalizedBinlog
        {
            public class Evaluation
            {
                public ProjectEvaluation ProjectEvaluation;
                public Dictionary<string, string> OriginalGlobalProperties;
                public Dictionary<string, string> StrippedProperties;
                public Dictionary<string, string> SpecificGlobalProperties;
                public string ConfigurationHash;

                public Dictionary<string, List<Target>> TargetsByName;
                public Dictionary<Project, List<Target>> TargetsByProject;

                public Evaluation(ProjectEvaluation projectEvaluation, List<Project> builds, HashSet<string> ignoreGlobalProperties)
                {
                    ProjectEvaluation = projectEvaluation;
                    OriginalGlobalProperties = projectEvaluation.GetGlobalProperties();
                    StrippedProperties = OriginalGlobalProperties.Where(kv => !ignoreGlobalProperties.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

                    TargetsByProject = builds.ToDictionary(t => t, t => t.Children.OfType<Target>().Where(t => t.Skipped == false).ToList());
                    TargetsByName = TargetsByProject.Values.SelectMany(t => t).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                }

                public void ComputeSpecificGlobalProperties(Dictionary<string, string> commonGlobalProperties)
                {
                    SpecificGlobalProperties = StrippedProperties
                        .Where(kv => !commonGlobalProperties.ContainsKey(kv.Key) || commonGlobalProperties[kv.Key] != kv.Value)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                }

                public void ComputeConfigurationHash()
                {
                    using (var hash = SHA256.Create())
                    {
                        ConfigurationHash = BitConverter.ToString(
                            hash.ComputeHash(Encoding.UTF8.GetBytes(
                                string.Join("\n", [
                                    ProjectEvaluation.ProjectFile.ToLowerInvariant(),
                                    ..SpecificGlobalProperties.OrderBy(t => t.Key).Select(kv => $"{kv.Key}={kv.Value}")
                                ])
                            ))
                        ).Replace("-", "");
                    }
                }

                public static string ToProjectFilePropertiesString(string projectFile, Dictionary<string, string> globalProperties)
                {
                    var sb = new StringBuilder();
                    sb.Append($"{projectFile}");

                    if (globalProperties.Any())
                    {
                        sb.Append(" {");
                        bool first = true;
                        foreach (var kv in globalProperties.OrderBy(kv => kv.Key))
                        {
                            if (!first)
                            {
                                sb.Append(", ");
                            }

                            first = false;

                            sb.Append($"{kv.Key} = {kv.Value.Replace("\n", "\\n")}");
                        }
                        sb.Append("}");
                    }
                    return sb.ToString();
                }

                public string ToOriginalString() => ToProjectFilePropertiesString(ProjectEvaluation.ProjectFile, OriginalGlobalProperties);

                public string ToSpecificString() => ToProjectFilePropertiesString(ProjectEvaluation.ProjectFile, SpecificGlobalProperties);
            }

            public Build Build;
            public Dictionary<string, string> CommonGlobalProperties;
            public Dictionary<int, Evaluation> EvaluationsById;
            public Dictionary<string, Evaluation> EvaluationsByConfigurationHash;

            public NormalizedBinlog(string path, HashSet<string> ignoreEvaluationsWith, HashSet<string> ignoreGlobalProperties)
            {
                Build = BinaryLog.ReadBuild(path);
                BuildAnalyzer.AnalyzeBuild(Build);

                var buildsByEvaluationId = Build.FindChildrenRecursive<Project>().GroupBy(t => t.EvaluationId).ToDictionary(t => t.Key, t => t.ToList());
                EvaluationsById = Build.EvaluationFolder.Children.OfType<ProjectEvaluation>().Select(evaluation => new Evaluation(evaluation, buildsByEvaluationId.TryGetValue(evaluation.Id, out var projectList) ? projectList : [], ignoreGlobalProperties)).ToDictionary(e => e.ProjectEvaluation.Id);
                var ignoredDueToProperty = EvaluationsById.Where(t => t.Value.OriginalGlobalProperties.Keys.Any(ignoreEvaluationsWith.Contains)).Select(t => t.Key).ToList();
                var ignoredDueToNoBuild = EvaluationsById.Where(t => !t.Value.TargetsByName.Any()).Select(t => t.Key).ToList();

                foreach (var id in ignoredDueToNoBuild.Distinct())
                {
                    Console.WriteLine($"Ignoring evaluation with no builds: Id:{id} Index:{EvaluationsById[id].ProjectEvaluation.Index}");
                    EvaluationsById.Remove(id);
                }

                foreach (var id in ignoredDueToProperty.Distinct())
                {
                    Console.WriteLine($"Ignoring evaluation due to property: Id:{id} Index:{EvaluationsById[id].ProjectEvaluation.Index}");
                    EvaluationsById.Remove(id);
                }

                CommonGlobalProperties = EvaluationsById.Values.Select(t => t.StrippedProperties).Aggregate((a, b) => a.Keys.Where(k => b.TryGetValue(k, out var v) && v == a[k]).ToDictionary(k => k, k => a[k]));

                foreach(var evaluation in EvaluationsById.Values)
                {
                    evaluation.ComputeSpecificGlobalProperties(CommonGlobalProperties);
                    evaluation.ComputeConfigurationHash();
                }

                EvaluationsByConfigurationHash = EvaluationsById.Values.GroupBy(e => e.ConfigurationHash).ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        if (g.Count() > 1)
                        {
                            Console.WriteLine($"Warning: Multiple evaluations with the same configuration hash {g.Key}:");

                            foreach (var eval in g)
                            {
                                Console.WriteLine($"  Before: {eval.ProjectEvaluation.Id} (Index:{eval.ProjectEvaluation.Index}: {eval.ToOriginalString()}");
                            }

                            Console.WriteLine($"  After: {g.First().ToSpecificString()}");
                        }

                        return g.First();
                    });
            }
        }

        public enum DifferenceLocation
        {
            OnlyLeft,
            Difference,
            OnlyRight,
        }

        public class Difference<T>
        {
            public DifferenceLocation Location;

            public T Left;
            public T Right;
        }

        public static void CompareDictionaries<K, T, U>(
            Dictionary<K, T> aDictionary,
            Dictionary<K, T> bDictionary,
            Func<K, T, U> order,
            Action<K, T, T> compare,
            Action<K, T, bool> missing)
        {
            var commonKeys = aDictionary.Keys.Intersect(bDictionary.Keys, aDictionary.Comparer).ToList();
            foreach (var key in commonKeys.OrderBy(key => order(key, aDictionary[key])))
            {
                compare(key, aDictionary[key], bDictionary[key]);
            }

            var onlyLeftKeys = aDictionary.Keys.Except(bDictionary.Keys, aDictionary.Comparer).ToList();
            foreach (var key in onlyLeftKeys.OrderBy(key => order(key, aDictionary[key])))
            {
                missing(key, aDictionary[key], false);
            }

            var onlyRightKeys = bDictionary.Keys.Except(aDictionary.Keys, aDictionary.Comparer).ToList();
            foreach (var key in onlyRightKeys.OrderBy(key => order(key, bDictionary[key])))
            {
                missing(key, bDictionary[key], true);
            }
        }

        public static void Compare(string binlogPath1, string binlogPath2, HashSet<string> ignoreEvaluationsWith, HashSet<string> ignoreGlobalProperties)
        {
            var normalized1 = new NormalizedBinlog(binlogPath1, ignoreEvaluationsWith, ignoreGlobalProperties);
            var normalized2 = new NormalizedBinlog(binlogPath2, ignoreEvaluationsWith, ignoreGlobalProperties);

            static string FormatEvalInfo(Evaluation e) => $"(EvaluationId:{e.ProjectEvaluation.Id}, EvaluationIndex:{e.ProjectEvaluation.Index})";
            static string FormatTargetInfo(Target t) => $"(ProjectIndex:{((Project)t.Parent).Index}, TargetIndex:{t.Index})";

            CompareDictionaries(normalized1.EvaluationsByConfigurationHash, normalized2.EvaluationsByConfigurationHash,
                static (hash, evaluation) => evaluation.ToSpecificString(),
                static (hash, evaluation1, evaluation2) =>
                {

                    Console.WriteLine($"{evaluation1.ToSpecificString()} [L:{FormatEvalInfo(evaluation1)}, R:{FormatEvalInfo(evaluation2)}]");

                    CompareDictionaries(evaluation1.TargetsByName, evaluation2.TargetsByName,
                        static (name, target) => target.Min(t => t.StartTime),
                        static (name, target1, target2) =>
                        {
                            Console.WriteLine($"  Target: {name}");

                            var executions1 = target1.Select((t, i) => new { Key = i, Value = t }).ToDictionary(t => t.Key, t => t.Value);
                            var executions2 = target2.Select((t, i) => new { Key = i, Value = t }).ToDictionary(t => t.Key, t => t.Value);

                            CompareDictionaries(
                                executions1,
                                executions2,
                                static (index, execution) => index,
                                static (index, execution1, execution2) =>
                                {

                                    Console.WriteLine($"    {execution1.Name} #{index}: [L:{FormatTargetInfo(execution1)}, R:{FormatTargetInfo(execution2)}]");

                                    var tasks1 = execution1.Children.OfType<Task>().GroupBy(t => t.Name).SelectMany(t => t.Select((u, i) => new { Key = (Name: t.Key, Index: i), Value = u })).ToDictionary(t => t.Key, t => t.Value);
                                    var tasks2 = execution2.Children.OfType<Task>().GroupBy(t => t.Name).SelectMany(t => t.Select((u, i) => new { Key = (Name: t.Key, Index: i), Value = u })).ToDictionary(t => t.Key, t => t.Value);

                                    CompareDictionaries(
                                        tasks1,
                                        tasks2,
                                        static (key, task) => task.StartTime,
                                        static (key, task1, task2) =>
                                        {
                                            Console.WriteLine($"      Task: {key.Name} #{key.Index}:");

                                            var task1Parameters = task1.GetParameters().ToDictionary(p => (p as Property)?.Name ?? (p as Parameter)?.Name, p => p, StringComparer.OrdinalIgnoreCase);
                                            var task2Parameters = task2.GetParameters().ToDictionary(p => (p as Property)?.Name ?? (p as Parameter)?.Name, p => p, StringComparer.OrdinalIgnoreCase);

                                            CompareDictionaries(
                                                task1Parameters,
                                                task2Parameters,
                                                static (paramName, parameter) => paramName,
                                                static (paramName, parameter1, parameter2) =>
                                                {
                                                    var values1 = (parameter1 as Parameter)?.Children.OfType<Item>().Select(t => t.Text).ToList() ?? new() { (parameter1 as Property).Value };
                                                    var values2 = (parameter2 as Parameter)?.Children.OfType<Item>().Select(t => t.Text).ToList() ?? new() { (parameter2 as Property).Value };

                                                    if (!values1.SequenceEqual(values2, StringComparer.OrdinalIgnoreCase))
                                                    {
                                                        string ValuesToString(IEnumerable<string> values) => string.Join(", ", values.Select(v => v.Replace("\n", "\\n")));

                                                        Console.WriteLine($"        [DIFF]: Parameter '{paramName}' different:");
                                                        Console.WriteLine($"          [L]: Value: {ValuesToString(values1)}");
                                                        Console.WriteLine($"          [R]: Value: {ValuesToString(values2)}");

                                                        if (values1.Count > 1 || values2.Count > 1)
                                                        {
                                                            var onlyIn1 = values1.Except(values2, StringComparer.OrdinalIgnoreCase).ToList();
                                                            var onlyIn2 = values2.Except(values1, StringComparer.OrdinalIgnoreCase).ToList();
                                                            var common = values1.Intersect(values2, StringComparer.OrdinalIgnoreCase).ToList();

                                                            if (onlyIn1.Any())
                                                            {
                                                                Console.WriteLine($"          [L]: L: {ValuesToString(onlyIn1)}");
                                                            }

                                                            if (common.Any())
                                                            {
                                                                Console.WriteLine($"          [R]: I: {ValuesToString(common)}");
                                                            }

                                                            if (onlyIn2.Any())
                                                            {
                                                                Console.WriteLine($"          [R]: R: {ValuesToString(onlyIn2)}");
                                                            }
                                                        }
                                                    }
                                                },
                                                static (paramName, parameter, isRight) =>
                                                {
                                                    Console.WriteLine($"        [{(isRight ? "R" : "L")}]: Parameter: {paramName}");
                                                }
                                            );

                                        },
                                        static (key, task, isRight) =>
                                        {
                                            Console.WriteLine($"      [{(isRight ? "R" : "L")}]: Task: {key.Name} #{key.Index}: (Index:{task.Index})");
                                        });
                                },
                                static (index, execution, isRight) =>
                                {
                                    Console.WriteLine($"    [{(isRight ? "R" : "L")}]: Execution #{index}: ({FormatTargetInfo(execution)})");
                                });

                        },
                        static (name, target, isRight) =>
                        {
                            Console.WriteLine($"  [{(isRight ? "R" : "L")}]: Target: {name} (Index:{string.Join(", ", target.Select(t => t.Index).OrderBy(t => t))})");
                        }
                    );
                },
                static (hash, evaluation, isRight) =>
                {
                    Console.WriteLine($"[{(isRight ? "R" : "L")}]: Evaluation: {FormatEvalInfo(evaluation)}: {evaluation.ToSpecificString()}");
                    Console.WriteLine($"  Project Indexes: {string.Join(", ", evaluation.TargetsByProject.OrderByDescending(t => t.Value.Count).Select(t => $"{t.Key.Index} ({t.Value.Count})"))}");
                });
        }
    }
}

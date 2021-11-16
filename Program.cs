using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;

namespace git_links_mapper
{
    class Config
    {
        public string SourceOrgUrl { get; set; }
        public string TargetOrgUrl { get; set; }
        public string SourceOrgPat { get; set; }
        public string TargetOrgPat { get; set; }
        public string TargetProjectName { get; set; }
        public string TargetAreaPath { get; set; }
    }
    class Program
    {
        static async Task Main(string[] args)
        {
            Config config = ParseConfig(args);

            var sourceConnection = new VssConnection(new Uri(config.SourceOrgUrl), new Microsoft.VisualStudio.Services.Common.VssBasicCredential("System Migrator", config.SourceOrgPat));
            var targetConnection = new VssConnection(new Uri(config.TargetOrgUrl), new Microsoft.VisualStudio.Services.Common.VssBasicCredential("System Migrator", config.TargetOrgPat));

            var targetWorkItemClient = targetConnection.GetClient<WorkItemTrackingHttpClient>();
            var sourceGitClient = sourceConnection.GetClient<GitHttpClient>();
            var targetGitClient = targetConnection.GetClient<GitHttpClient>();

            var queryForItems = new Wiql();
            queryForItems.Query = @$"
                SELECT
                    [System.Id],
                    [System.WorkItemType],
                    [System.Title],
                    [System.AssignedTo],
                    [System.State],
                    [System.Tags]
                FROM workitems
                WHERE
                    [System.TeamProject] = @project
                    AND [System.ExternalLinkCount] > 0
                    AND [Custom.ReflectedWorkItemId] <> ''
                    AND [System.AreaPath] UNDER '{config.TargetAreaPath}'
                ORDER BY [System.Id]
            ";

            // Let's get target work items
            var workItemIds = await targetWorkItemClient.QueryByWiqlAsync(queryForItems, project: config.TargetProjectName, top: 19999);
            var targetRepos = await targetGitClient.GetRepositoriesAsync(config.TargetProjectName);
            var sourceRepos = await sourceGitClient.GetRepositoriesAsync();

            Console.WriteLine($"Found [{workItemIds.WorkItems.Count()}] work items in project [{config.TargetProjectName}] that contain external links and were migrated..");

            foreach (var result in workItemIds.WorkItems)
            {
                //Console.WriteLine($"    [{result.Id}] - Starting mapping");

                var workItem = await targetWorkItemClient.GetWorkItemAsync(result.Id, expand: WorkItemExpand.All);
                JsonPatchDocument jsonPatchDoc = new JsonPatchDocument();

                if (workItem.Relations != null && workItem.Relations.Count > 0)
                {
                    var relIx = 0;
                    foreach (var relation in workItem.Relations)
                    {
                        var gitArtifactPrefix = "vstfs:///Git/";
                        if (relation.Url.StartsWith(gitArtifactPrefix))
                        {
                            // It's a git link.. let's figure out the mapping
                            // ie. vstfs:///Git/PullRequestId/9cc50893-cbfa-43ed-a3e5-5dc2ab8d1010%2f46fe0a34-28cc-404c-b5d3-feec74d95abe%2f5938
                            var gitLinkType = relation.Url.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(2).FirstOrDefault();
                            var split = relation.Url.Split('/').Last().Split(new string[] { "%2f", "%2F" }, StringSplitOptions.RemoveEmptyEntries);
                            var projectId = split.Take(1).FirstOrDefault();
                            var repoId = split.Skip(1).Take(1).FirstOrDefault();
                            var refId = string.Join('/', split.Skip(2));

                            GitRepository sourceRepo = null;
                            GitRepository targetRepo = null;

                            try
                            {
                                sourceRepo = sourceRepos.FirstOrDefault(r => r.Id.ToString().Equals(repoId, StringComparison.OrdinalIgnoreCase));

                            }
                            catch (VssServiceException ex)
                            {
                                if (ex.Message.Contains("TF401019")) 
                                {
                                    Console.WriteLine($"ERROR: Could not locate source repo [{repoId}] linked from work item [{workItem.Id}].");
                                    continue;
                                }
                                throw new Exception("Failed to get source repo.", ex);
                            }

                            foreach (var tRepo in targetRepos)
                            {
                                if (tRepo.Name.EndsWith(sourceRepo.Name) || tRepo.Name.EndsWith(sourceRepo.Name.Replace(" ", "_")))
                                {
                                    targetRepo = tRepo;
                                    break;
                                }
                            }

                            if (targetRepo != null)
                            {
                                var attr = relation.Attributes.ToDictionary(x => x.Key.ToString(), x => x.Value);
                                attr.Remove("id");
                                jsonPatchDoc.Add(new JsonPatchOperation()
                                {
                                    Operation = Operation.Remove,
                                    Path = $"/relations/{relIx}"
                                });
                                jsonPatchDoc.Add(new JsonPatchOperation()
                                {
                                    Operation = Operation.Add,
                                    Path = $"/relations/-",
                                    Value = new WorkItemRelation()
                                    {
                                        Rel = relation.Rel,
                                        Url = $"{gitArtifactPrefix}{gitLinkType}/{string.Join("%2f", new string[] { projectId, repoId, refId })}",
                                        Attributes = attr
                                    }
                                });
                            }
                            else
                            {
                                Console.WriteLine($"ERROR: Failed to map {sourceRepo.Name}");
                            }
                        }

                        relIx += 1;
                    }
                    if (jsonPatchDoc.Count > 0)
                    {
                        System.IO.File.WriteAllText("./test.json", System.Text.Json.JsonSerializer.Serialize(jsonPatchDoc));
                        Console.WriteLine($"      Removing [{jsonPatchDoc.Count(x => x.Operation == Operation.Remove)}] relations.");
                        Console.WriteLine($"      Adding [{jsonPatchDoc.Count(x => x.Operation == Operation.Add)}] relations.");

                        await targetWorkItemClient.UpdateWorkItemAsync(jsonPatchDoc, workItem.Id.Value, bypassRules: true, suppressNotifications: true);

                    }
                }
            }

        }
        static Config ParseConfig(string[] args)
        {

            Config config = new Config();
            try
            {
                config.SourceOrgUrl = args[0];
                config.SourceOrgPat = args[1];
                config.TargetOrgUrl = args[2];
                config.TargetOrgPat = args[3];
                config.TargetProjectName = args[4];
                config.TargetAreaPath = args[5];
            }
            catch (Exception ex)
            {
                PrintHelp();
                Console.WriteLine("Errors listed out below:");
                throw new Exception($"Error: Failed to parse command line parameters. ({ex.Message})", ex);
            }
            return config;
        }
        static void PrintHelp() => Console.WriteLine($"Example: git-links-mapper.exe <source org url> <source org pat> <target org url> <target org pat> <target project");
    }
}

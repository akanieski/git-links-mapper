using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.Core.WebApi;
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
        public string TypeFilter { get; set; }
    }
    class Program
    {
        static int BATCH_SIZE = 1000;
        static async Task Main(string[] args)
        {
            Config config = ParseConfig(args);

            var sourceConnection = new VssConnection(new Uri(config.SourceOrgUrl), new Microsoft.VisualStudio.Services.Common.VssBasicCredential("System Migrator", config.SourceOrgPat));
            var targetConnection = new VssConnection(new Uri(config.TargetOrgUrl), new Microsoft.VisualStudio.Services.Common.VssBasicCredential("System Migrator", config.TargetOrgPat));

            var targetProjectClient = targetConnection.GetClient<ProjectHttpClient>();
            var targetWorkItemClient = targetConnection.GetClient<WorkItemTrackingHttpClient>();
            var sourceGitClient = sourceConnection.GetClient<GitHttpClient>();
            var targetGitClient = targetConnection.GetClient<GitHttpClient>();

            int currentId = 0;
            bool hasMore = true;

            while (hasMore)
            {
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
                    {(currentId == 0 ? $"" : $" AND [System.ID] > {currentId}")}
                    AND [System.WorkItemType] = '{config.TypeFilter}'
                    AND [System.ExternalLinkCount] > 0
                    AND [System.AreaPath] UNDER '{config.TargetAreaPath}'
                ORDER BY [System.Id]
            ";

                // Let's get target work items
                var workItemIds = await targetWorkItemClient.QueryByWiqlAsync(queryForItems, project: config.TargetProjectName, top: BATCH_SIZE);
                hasMore = workItemIds.WorkItems.Count() == BATCH_SIZE;

                var targetProjects = await targetProjectClient.GetProjects();
                var targetRepos = await targetGitClient.GetRepositoriesAsync();
                var sourceRepos = await sourceGitClient.GetRepositoriesAsync();
                var targetProject = targetProjects.FirstOrDefault(p => p.Name.Equals(config.TargetProjectName, StringComparison.Ordinal));

                Console.WriteLine($"Processing batch of [{workItemIds.WorkItems.Count()}] work items in project [{config.TargetProjectName}] that contain external links and were migrated..");

                foreach (var result in workItemIds.WorkItems)
                {
                    var workItem = await targetWorkItemClient.GetWorkItemAsync(result.Id, expand: WorkItemExpand.All);
                    JsonPatchDocument jsonPatchDoc = new JsonPatchDocument();
                    var prLinks = new List<(int prNum, string url, string originalUrl)>();

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

                                if (gitLinkType.ToLower() == "pullrequestid")
                                {
                                    #region Handle Pull Request based git links ..

                                    var pr = await sourceGitClient.GetPullRequestAsync(projectId, repoId, int.Parse(refId), 999, includeCommits: true, includeWorkItemRefs: true);
                                    var attachmentRef = await targetWorkItemClient.CreateAttachmentAsync(
                                        new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes(System.Text.Json.JsonSerializer.Serialize(pr))),
                                        fileName: $"pr-summary-{refId}.json",
                                        uploadType: "Simple");
                                    jsonPatchDoc.Add(new JsonPatchOperation()
                                    {
                                        Operation = Operation.Remove,
                                        Path = $"/relations/{relIx}"
                                    });
                                    jsonPatchDoc.Add(new JsonPatchOperation()
                                    {
                                        Operation = Operation.Add,
                                        Path = "/relations/-",
                                        Value = new WorkItemRelation()
                                        {
                                            Rel = "AttachedFile",
                                            Url = attachmentRef.Url,
                                            Attributes = new Dictionary<string, object>() {
                                                    {"comment", $"PR Summary Migrated From Source PR #{refId}"}
                                                }
                                        }
                                    });
                                    jsonPatchDoc.Add(new JsonPatchOperation()
                                    {
                                        Operation = Operation.Add,
                                        Path = "/relations/-",
                                        Value = new WorkItemRelation()
                                        {
                                            Rel = "Hyperlink",
                                            Url = attachmentRef.Url,
                                            Attributes = new Dictionary<string, object>() {
                                                    {"comment", $"PR Summary Migrated From Source PR #{refId}"}
                                                }
                                        }
                                    });
                                    
                                        var sourceRepo = sourceRepos.FirstOrDefault(r => r.Id.ToString().Equals(repoId, StringComparison.OrdinalIgnoreCase));
                                        
                                    prLinks.Add((
                                        int.Parse(refId), 
                                        attachmentRef.Url, 
                                        sourceRepo == null ? "" : $"{config.SourceOrgUrl}/{sourceRepo.ProjectReference.Name}/_git/{sourceRepo.Name}/pullrequest/{refId}"));
                                    continue;
                                    #endregion
                                }
                                else
                                {
                                    #region Handle Branch/Commit based git links ..
                                    if (targetProjects.Any(x => x.Id.ToString().Equals(projectId, StringComparison.Ordinal)))
                                    {
                                        // repo already exists on target.. nothing to map .. move on
                                        continue;
                                    }
                                    GitRepository sourceRepo = null;
                                    GitRepository targetRepo = null;

                                    try
                                    {
                                        sourceRepo = sourceRepos.FirstOrDefault(r => r.Id.ToString().Equals(repoId, StringComparison.OrdinalIgnoreCase));

                                        if (sourceRepo == null)
                                        {
                                            Console.WriteLine($"ERROR: Could not locate source repo [{repoId}] linked from work item [{workItem.Id}].");
                                            continue;
                                        }

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

                                    var matchedRepos = new List<GitRepository>();
                                    foreach (var tRepo in targetRepos)
                                    {
                                        if (tRepo.Name.EndsWith(sourceRepo.Name) || tRepo.Name.EndsWith(sourceRepo.Name.Replace(" ", "_")))
                                        {
                                            matchedRepos.Add(tRepo);
                                        }
                                    }

                                    if (matchedRepos.Count > 1)
                                    {
                                        var skip = false;
                                        while (targetRepo == null)
                                        {
                                            Console.WriteLine($"Repo [{sourceRepo.Name}] has matched to {matchedRepos.Count} repos in target.");
                                            Console.WriteLine($"Select the correct mapping below:");
                                            for (var i = 1; i <= matchedRepos.Count; i++)
                                            {
                                                Console.WriteLine($"    [{i}] - {matchedRepos[i - 1].ProjectReference.Name}\\{matchedRepos[i - 1].Name} ");
                                            }
                                            Console.WriteLine($"    [S] - Skip this repo ");
                                            Console.WriteLine($"Enter an option to continue (1-{matchedRepos.Count}): ");

                                            var input = Console.ReadKey();


                                            if (input.KeyChar == 's' || input.KeyChar == 'S')
                                            {
                                                Console.WriteLine($"\r\nSkipped [{sourceRepo.Name}]");
                                                skip = true;
                                                break;
                                            }
                                            else
                                            {
                                                int.TryParse(new char[] { input.KeyChar }, out int selection);

                                                if (selection > 0 && selection <= matchedRepos.Count)
                                                {
                                                    targetRepo = matchedRepos[selection - 1];
                                                    Console.WriteLine($"\r\nMapped [{sourceRepo.Name}] to [{targetRepo.Name}] in target.");
                                                    break;
                                                }
                                                else
                                                {
                                                    Console.WriteLine("\r\nInvalid input. Try again!");
                                                    continue;
                                                }
                                            }
                                        }
                                        if (skip) continue;
                                    }
                                    else
                                    {
                                        targetRepo = matchedRepos.FirstOrDefault();
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
                                                Url = $"{gitArtifactPrefix}{gitLinkType}/{string.Join("%2f", new string[] { targetRepo.ProjectReference.Id.ToString(), targetRepo.Id.ToString(), refId })}",
                                                Attributes = attr
                                            }
                                        });
                                    }
                                    else
                                    {
                                        Console.WriteLine($"WARNING: Could not map {sourceRepo.Name} in target org.");
                                    }
                                    #endregion
                                }
                            }

                            relIx += 1;
                        }
                        if (jsonPatchDoc.Count > 0)
                        {
                            System.IO.File.WriteAllText("./test.json", System.Text.Json.JsonSerializer.Serialize(jsonPatchDoc));
                            Console.WriteLine($"      Removing [{jsonPatchDoc.Count(x => x.Operation == Operation.Remove)}] relations.");
                            Console.WriteLine($"      Adding [{jsonPatchDoc.Count(x => x.Operation == Operation.Add)}] relations.");
                            try
                            {
                                await targetWorkItemClient.UpdateWorkItemAsync(jsonPatchDoc, workItem.Id.Value);

                                if (prLinks.Count > 0)
                                {
                                    await targetWorkItemClient.AddCommentAsync(new CommentCreate()
                                    {
                                        Text = $"Work item migrated with links to the following pull requests: <br><ul>" 
                                            + string.Join("<br>", prLinks.Select(pr => pr.originalUrl != "" 
                                            ? $"<li><a href='{pr.originalUrl}' >Original PR #{pr.prNum}</a> - <a href='{pr.url}'>Raw Data</a></li>"
                                            : $"<li><a>Original PR #{pr.prNum}</a> - <a href='{pr.url}'>Raw Data</a></li>"))
                                    }, targetProject.Id, workItem.Id.Value);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (ex.Message.Contains("Relation already exists."))
                                {
                                    continue;
                                }
                            }

                        }
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
                config.TypeFilter = args[6];
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

﻿using ShipItSharp.Core.Deployment.Interfaces;
using ShipItSharp.Core.Interfaces;
using ShipItSharp.Core.JobRunners.JobConfigs;
using ShipItSharp.Core.Language;
using ShipItSharp.Core.Logging.Interfaces;
using ShipItSharp.Core.Models;
using ShipItSharp.Core.Octopus.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShipItSharp.Core.JobRunners
{
    public class PromotionRunner
    {
        private readonly ILanguageProvider _languageProvider;
        private readonly IOctopusHelper helper;
        private readonly IDeployer deployer;
        private readonly IUiLogger uiLogger;

        private PromotionConfig _currentConfig;
        private IProgressBar progressBar;

        public PromotionRunner(ILanguageProvider languageProvider, IOctopusHelper helper, IDeployer deployer, IUiLogger uiLogger)
        {
            this._languageProvider = languageProvider;
            this.helper = helper;
            this.deployer = deployer;
            this.uiLogger = uiLogger;
        }

        public async Task<int> Run(PromotionConfig config, IProgressBar progressBar, Func<PromotionConfig, (List<Project> currentProjects, List<Project> targetProjects), IEnumerable<int>> setDeploymentProjects, Func<string, string> userPrompt)
        {
            this._currentConfig = config;
            this.progressBar = progressBar;

            var (projects, targetProjects) = await GenerateProjectList();

            List<int> indexes = new List<int>();

            if (config.RunningInteractively)
            {
                indexes.AddRange(setDeploymentProjects(_currentConfig, (projects, targetProjects)));
            }
            else 
            {
                for (int i = 0; i < projects.Count(); i++)
                {
                    if (projects[i].Checked)
                    {
                        indexes.Add(i);
                    }
                }
            }

            var deployment = GenerateEnvironmentDeployment(indexes, projects, targetProjects);

            if (deployment == null)
            {
                return -1;
            }

            var result = await this.deployer.CheckDeployment(deployment);
            if (!result.Success)
            {
                System.Console.WriteLine(_languageProvider.GetString(LanguageSection.UiStrings, "Error") + result.ErrorMessage);
                return -1;
            }

            deployer.FillRequiredVariables(deployment.ProjectDeployments, userPrompt, _currentConfig.RunningInteractively);

            await this.deployer.StartJob(deployment, this.uiLogger);

            return 0;
        }

        private EnvironmentDeployment GenerateEnvironmentDeployment(IEnumerable<int> indexes, List<Project> projects, List<Project> targetProjects)
        {
            if (!indexes.Any())
            {
                System.Console.WriteLine(_languageProvider.GetString(LanguageSection.UiStrings, "NothingSelected"));
                return null;
            }

            var deployment = new EnvironmentDeployment
            {
                ChannelName = string.Empty,
                DeployAsync = true,
                EnvironmentId = _currentConfig.DestinationEnvironment.Id,
                EnvironmentName = _currentConfig.DestinationEnvironment.Name
            };

            foreach (var index in indexes)
            {
                var current = projects[index];
                var currentTarget = targetProjects[index];

                if (current.CurrentRelease != null)
                {
                    deployment.ProjectDeployments.Add(new ProjectDeployment
                    {
                        ProjectId = currentTarget.ProjectId,
                        ProjectName = currentTarget.ProjectName,
                        LifeCycleId = currentTarget.LifeCycleId,
                        ReleaseId = current.CurrentRelease.Id
                    });
                }
            }

            return deployment;
        }

        private async Task<(List<Project> projects, List<Project> targetProjects)> GenerateProjectList()
        {
            var projects = new List<Project>();
            var targetProjects = new List<Project>();

            progressBar.WriteStatusLine(_languageProvider.GetString(LanguageSection.UiStrings, "FetchingProjectList"));
            var projectStubs = await helper.Projects.GetProjectStubs();

            var groupIds = new List<string>();
            if (!string.IsNullOrEmpty(_currentConfig.GroupFilter))
            {
                progressBar.WriteStatusLine(_languageProvider.GetString(LanguageSection.UiStrings, "GettingGroupInfo"));
                groupIds =
                    (await helper.Projects.GetFilteredProjectGroups(_currentConfig.GroupFilter))
                    .Select(g => g.Id).ToList();
            }

            progressBar.CleanCurrentLine();

            foreach (var projectStub in projectStubs)
            {
                progressBar.WriteProgress(projectStubs.IndexOf(projectStub) + 1, projectStubs.Count(),
                    String.Format(_languageProvider.GetString(LanguageSection.UiStrings, "LoadingInfoFor"), projectStub.ProjectName));
                if (!string.IsNullOrEmpty(_currentConfig.GroupFilter))
                {
                    if (!groupIds.Contains(projectStub.ProjectGroupId))
                    {
                        continue;
                    }
                }

                var project = await helper.Projects.ConvertProject(projectStub, _currentConfig.SourceEnvironment.Id, null, null);
                var targetProject = await helper.Projects.ConvertProject(projectStub, _currentConfig.DestinationEnvironment.Id, null, null);

                var currentRelease = project.CurrentRelease;
                var currentTargetRelease = targetProject.CurrentRelease;
                if (currentRelease == null)
                {
                    continue;
                }

                if (currentTargetRelease != null && currentTargetRelease.Id == currentRelease.Id)
                {
                    project.Checked = false;
                }
                else
                {
                    project.Checked = true;
                }

                projects.Add(project);
                targetProjects.Add(targetProject);
            }

            progressBar.CleanCurrentLine();

            return (projects, targetProjects);
        }
    }
}

﻿#region copyright
/*
    ShipItSharp Deployment Coordinator. Provides extra tooling to help 
    deploy software through Octopus Deploy.

    Copyright (C) 2018  Steven Davies

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
#endregion


using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using ShipItSharp.Console.Commands.SubCommands;
using ShipItSharp.Console.ConsoleTools;
using ShipItSharp.Core.Configuration.Interfaces;
using ShipItSharp.Core.Deployment.Models;
using ShipItSharp.Core.Interfaces;
using ShipItSharp.Core.JobRunners;
using ShipItSharp.Core.JobRunners.JobConfigs;
using ShipItSharp.Core.Language;
using ShipItSharp.Core.Octopus.Interfaces;

namespace ShipItSharp.Console.Commands
{
    internal class Deploy : BaseCommand
    {
        private readonly DeploySpecific _deploySpecific;
        private readonly DeployWithProfile _profile;
        private readonly DeployWithProfileDirectory _profileDir;
        private readonly DeployRunner _runner;
        private readonly IConfiguration _configuration;
        private readonly IProgressBar _progressBar;

        public Deploy(DeployRunner deployRunner, IConfiguration configuration, IOctopusHelper octoHelper, DeployWithProfile profile, DeployWithProfileDirectory profileDir, DeploySpecific deploySpecific, IProgressBar progressBar, ILanguageProvider languageProvider) : base(octoHelper, languageProvider)
        {
            this._configuration = configuration;
            this._profile = profile;
            this._profileDir = profileDir;
            this._progressBar = progressBar;
            _runner = deployRunner;
            this._deploySpecific = deploySpecific;
        }

        protected override bool SupportsInteractiveMode => true;
        public override string CommandName => "deploy";

        public override void Configure(CommandLineApplication command)
        {
            base.Configure(command);
            command.Description = LanguageProvider.GetString(LanguageSection.OptionsStrings, "DeployProjects");

            ConfigureSubCommand(_profile, command);
            ConfigureSubCommand(_profileDir, command);
            ConfigureSubCommand(_deploySpecific, command);

            AddToRegister(DeployOptionNames.ChannelName, command.Option("-c|--channel", LanguageProvider.GetString(LanguageSection.OptionsStrings, "DeployChannel"), CommandOptionType.SingleValue));
            AddToRegister(DeployOptionNames.Environment, command.Option("-e|--environment", LanguageProvider.GetString(LanguageSection.OptionsStrings, "EnvironmentName"), CommandOptionType.SingleValue));
            AddToRegister(DeployOptionNames.GroupFilter, command.Option("-g|--groupfilter", LanguageProvider.GetString(LanguageSection.OptionsStrings, "GroupFilter"), CommandOptionType.SingleValue));
            AddToRegister(DeployOptionNames.SaveProfile, command.Option("-s|--saveprofile", LanguageProvider.GetString(LanguageSection.OptionsStrings, "SaveProfile"), CommandOptionType.SingleValue));
            AddToRegister(DeployOptionNames.DefaultFallback, command.Option("-d|--fallbacktodefault", LanguageProvider.GetString(LanguageSection.OptionsStrings, "FallbackToDefault"), CommandOptionType.NoValue));
            AddToRegister(OptionNames.ReleaseName, command.Option("-r|--releasename", LanguageProvider.GetString(LanguageSection.OptionsStrings, "ReleaseVersion"), CommandOptionType.SingleValue));
        }


        protected override async Task<int> Run(CommandLineApplication command)
        {
            var profilePath = GetStringValueFromOption(DeployOptionNames.SaveProfile);
            if (!string.IsNullOrEmpty(profilePath))
            {
                System.Console.WriteLine(LanguageProvider.GetString(LanguageSection.UiStrings, "GoingToSaveProfile"), profilePath);
            }
            _progressBar.WriteStatusLine(LanguageProvider.GetString(LanguageSection.UiStrings, "FetchingProjectList"));
            var projectStubs = await OctoHelper.Projects.GetProjectStubs();
            var found = projectStubs.Where(proj => _configuration.ChannelSeedProjectNames.Select(c => c.ToLower()).Contains(proj.ProjectName.ToLower()));

            if (!found.Any())
            {
                System.Console.WriteLine(LanguageProvider.GetString(LanguageSection.UiStrings, "ProjectNotFound"));
                return -1;
            }

            var channelName = GetStringFromUser(DeployOptionNames.ChannelName, LanguageProvider.GetString(LanguageSection.UiStrings, "WhichChannelPrompt"));
            var environmentName = GetStringFromUser(DeployOptionNames.Environment, LanguageProvider.GetString(LanguageSection.UiStrings, "WhichEnvironmentPrompt"));
            var groupRestriction = GetStringFromUser(DeployOptionNames.GroupFilter, LanguageProvider.GetString(LanguageSection.UiStrings, "RestrictToGroupsPrompt"), true);
            var forceDefault = GetOption(DeployOptionNames.DefaultFallback).HasValue();

            _progressBar.WriteStatusLine(LanguageProvider.GetString(LanguageSection.UiStrings, "CheckingOptions"));

            var environment = await FetchEnvironmentFromUserInput(environmentName);

            if (environment == null)
            {
                return -2;
            }

            Core.Deployment.Models.Channel channel = null;
            foreach (var project in found)
            {
                channel = await OctoHelper.Channels.GetChannelByProjectNameAndChannelName(project.ProjectName, channelName);
                if (channel != null)
                {
                    break;
                }
            }

            if (channel == null)
            {
                System.Console.WriteLine(LanguageProvider.GetString(LanguageSection.UiStrings, "NoMatchingChannel"));
                return -1;
            }

            Core.Deployment.Models.Channel defaultChannel = null;

            if (forceDefault && !string.IsNullOrEmpty(_configuration.DefaultChannel))
            {
                defaultChannel = await OctoHelper.Channels.GetChannelByProjectNameAndChannelName(found.First().ProjectName, _configuration.DefaultChannel);
            }

            var configResult = DeployConfig.Create(environment, channel, defaultChannel, groupRestriction, GetStringValueFromOption(DeployOptionNames.SaveProfile), InInteractiveMode);


            if (configResult.IsFailure)
            {
                System.Console.WriteLine(configResult.Error);
                return -1;
            }
            return await _runner.Run(configResult.Value, _progressBar, projectStubs, InteractivePrompt, PromptForStringWithoutQuitting, text => { return Prompt.GetString(text); });
        }

        private IEnumerable<int> InteractivePrompt(DeployConfig config, IList<Project> projects)
        {
            var runner = PopulateRunner(string.Format(LanguageProvider.GetString(LanguageSection.UiStrings, "DeployingTo"), config.Channel.Name, config.Environment.Name), LanguageProvider.GetString(LanguageSection.UiStrings, "PackageNotSelectable"), projects);
            return runner.GetSelectedIndexes();
        }


        private InteractiveRunner PopulateRunner(string prompt, string unselectableText, IEnumerable<Project> projects)
        {
            var runner = new InteractiveRunner(prompt,
                unselectableText,
                LanguageProvider,
                LanguageProvider.GetString(LanguageSection.UiStrings, "ProjectName"),
                LanguageProvider.GetString(LanguageSection.UiStrings, "CurrentRelease"),
                LanguageProvider.GetString(LanguageSection.UiStrings, "CurrentPackage"),
                LanguageProvider.GetString(LanguageSection.UiStrings, "NewPackage"),
                LanguageProvider.GetString(LanguageSection.UiStrings, "OldestPackagePublish"),
                LanguageProvider.GetString(LanguageSection.UiStrings, "PackageAgeDays")
            );

            foreach (var project in projects)
            {
                var packagesAvailable = (project.AvailablePackages.Count > 0) && project.AvailablePackages.All(p => p.SelectedPackage != null);

                DateTime? lastModified = null;

                foreach (var package in project.AvailablePackages)
                {
                    if (((lastModified == null) && (package.SelectedPackage != null) && package.SelectedPackage.PublishedOn.HasValue) || ((package.SelectedPackage != null) && package.SelectedPackage.PublishedOn.HasValue && (package.SelectedPackage.PublishedOn < lastModified)))
                    {
                        lastModified = package.SelectedPackage.PublishedOn;
                    }
                }

                runner.AddRow(project.Checked, packagesAvailable, project.ProjectName, project.CurrentRelease.Version, project.AvailablePackages.Count > 1 ? LanguageProvider.GetString(LanguageSection.UiStrings, "Multi") : project.CurrentRelease.DisplayPackageVersion, project.AvailablePackages.Count > 1 ? LanguageProvider.GetString(LanguageSection.UiStrings, "Multi") :
                    packagesAvailable ? project.AvailablePackages.First().SelectedPackage.Version : string.Empty, lastModified.HasValue ? $"{lastModified.Value.ToShortDateString()} : {lastModified.Value.ToShortTimeString()}" : string.Empty,
                    lastModified.HasValue ? $"{DateTime.Now.Subtract(lastModified.Value).Days.ToString()}{(lastModified.Value < DateTime.Now.AddDays(-7) ? "*" : string.Empty)}" : string.Empty);

            }
            runner.Run();
            return runner;
        }

        private struct DeployOptionNames
        {
            public const string ChannelName = "channel";
            public const string Environment = "environment";
            public const string GroupFilter = "groupfilter";
            public const string SaveProfile = "saveprofile";
            public const string DefaultFallback = "fallbacktodefault";
        }
    }
}
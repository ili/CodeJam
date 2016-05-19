﻿using System;

using BenchmarkDotNet.Competitions;

using JetBrains.Annotations;

namespace BenchmarkDotNet.Configs
{
	[PublicAPI]
	public class ManualCompetitionConfig : ManualConfig, ICompetitionConfig
	{
		public static ManualCompetitionConfig Union(ICompetitionConfig globalConfig, ICompetitionConfig localConfig)
		{
			var competitionConfig = new ManualCompetitionConfig();
			switch (localConfig.UnionRule)
			{
				case ConfigUnionRule.AlwaysUseLocal:
					competitionConfig.Add(localConfig);
					break;
				case ConfigUnionRule.AlwaysUseGlobal:
					competitionConfig.Add(globalConfig);
					break;
				case ConfigUnionRule.Union:
					competitionConfig.Add(globalConfig);
					competitionConfig.Add(localConfig);
					break;
			}
			return competitionConfig;
		}

		#region Ctor & Add()
		public ManualCompetitionConfig() { }

		public ManualCompetitionConfig(IConfig config)
		{
			if (config != null)
			{
				var competition = config as ICompetitionConfig;
				if (competition != null)
				{
					Add(competition);
				}
				else
				{
					base.Add(config);
				}
			}
		}

		public void Add(ICompetitionConfig config)
		{
			Add((IConfig)config);

			// Runner config
			DetailedLogging = config.DetailedLogging;
			DisableValidation = config.DisableValidation;
			MaxRunsAllowed = config.MaxRunsAllowed;

			// Validation config
			AllowSlowBenchmarks = config.AllowSlowBenchmarks;
			DefaultCompetitionLimit = config.DefaultCompetitionLimit;
			EnableReruns = config.EnableReruns;

			// Annotation config
			AnnotateOnRun = config.AnnotateOnRun;
			IgnoreExistingAnnotations = config.IgnoreExistingAnnotations;
			LogAnnotationResults = config.LogAnnotationResults;
		}
		#endregion

		// Runner config
		public bool DetailedLogging { get; set; }
		public bool DisableValidation { get; set; }
		public int MaxRunsAllowed { get; set; } = 10;

		// Validation config
		public bool AllowSlowBenchmarks { get; set; }
		public CompetitionLimit DefaultCompetitionLimit { get; set; }
		public bool EnableReruns { get; set; }

		// Annotation config
		public bool AnnotateOnRun { get; set; }
		public bool IgnoreExistingAnnotations { get; set; }
		public bool LogAnnotationResults { get; set; }
	}
}
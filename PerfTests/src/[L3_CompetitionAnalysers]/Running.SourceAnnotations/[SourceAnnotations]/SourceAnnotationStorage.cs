﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using BenchmarkDotNet.Running;

using CodeJam.Collections;
using CodeJam.PerfTests.Analysers;
using CodeJam.PerfTests.Configs;
using CodeJam.PerfTests.Metrics;
using CodeJam.PerfTests.Running.Core;
using CodeJam.PerfTests.Running.Messages;
using CodeJam.Ranges;
using CodeJam.Reflection;
using CodeJam.Strings;
using JetBrains.Annotations;

using static CodeJam.PerfTests.Running.SourceAnnotations.SourceAnnotationHelpers;

namespace CodeJam.PerfTests.Running.SourceAnnotations
{
	/// <summary>
	/// Source annotations storage
	/// </summary>
	/// <seealso cref="CodeJam.PerfTests.Running.SourceAnnotations.IAnnotationStorage" />
	internal class SourceAnnotationStorage : AnnotationStorageBase
	{
		/// <summary>Retrieves stored info for competition targets.</summary>
		/// <param name="targets">Competition targets the metrics are retrieved for.</param>
		/// <param name="analysis">State of the analysis.</param>
		/// <returns>Stored info for competition targets.</returns>
		protected override IReadOnlyDictionary<Target, StoredTargetInfo> GetStoredTargets(Target[] targets, Analysis analysis)
		{
			var result = new Dictionary<Target, StoredTargetInfo>();
			var metrics = analysis.Config.GetMetrics().ToArray();
			foreach (var target in targets)
			{
				var storedTarget = TryGetStoredTarget(target, metrics, analysis);

				if (storedTarget != null)
				{
					result.Add(target, storedTarget);
				}
			}
			return result;
		}

		[CanBeNull]
		private static StoredTargetInfo TryGetStoredTarget(
			Target target, MetricInfo[] metrics, IMessageLogger messageLogger)
		{
			var metricsByType = metrics.ToDictionary(m => m.AttributeType);
			var result = new List<StoredMetricValue>(metricsByType.Count);

			foreach (var metricAttribute in target.Method.GetMetadataAttributes<IStoredMetricValue>(true).ToArray())
			{
				if (!metricsByType.ContainsKey(metricAttribute.MetricAttributeType))
				{
					// TODO: improve check for primary metric?
					if (metricAttribute.MetricAttributeType != MetricInfo.PrimaryMetric.AttributeType)
					{
						// TODO: common API for unknown metrics, refactor it to base?
						var metricName = metricAttribute.MetricAttributeType.GetShortAttributeName();
						messageLogger.WriteInfoMessage(
							target,
							$"Metric {metricName} not listed in config and therefore is ignored.",
							$"List of metrics is exposed as {nameof(ICompetitionConfig)}.{nameof(ICompetitionConfig.GetMetrics)}().");
					}
					continue;
				}

				// Transport object allows to not hold ref to the attribute.
				var storedMetric = new StoredMetricValue(
					metricAttribute.MetricAttributeType,
					metricAttribute.Min,
					metricAttribute.Max,
					metricAttribute.UnitOfMeasurement);

				result.Add(storedMetric);
			}
			return new StoredTargetInfo(result.ToArray());
		}

		/// <summary>Saves stored metrics from competition targets.</summary>
		/// <param name="competitionTargets">Competition targets with metrics to save.</param>
		/// <param name="annotationContext">The annotation context.</param>
		/// <param name="analysis">State of the analysis.</param>
		/// <returns>Saved targets, if any.</returns>
		protected override CompetitionTarget[] TrySaveAnnotationsCore(
			IReadOnlyCollection<CompetitionTarget> competitionTargets,
			AnnotationContext annotationContext,
			Analysis analysis)
		{
			var result = new List<CompetitionTarget>();
			foreach (var targetToAnnotate in competitionTargets)
			{
				TrySaveAnnotationCore(targetToAnnotate, annotationContext, result, analysis);
			}
			return result.ToArray();
		}

		private static void TrySaveAnnotationCore(
			CompetitionTarget targetToAnnotate,
			AnnotationContext annotationContext,
			List<CompetitionTarget> result,
			IMessageLogger messageLogger)
		{
			var metrics = targetToAnnotate.MetricValues.Where(m => m.HasUnsavedChanges).ToArray();
			if (metrics.Length == 0)
				return;

			var target = targetToAnnotate.Target;
			var targetKey = new AnnotationTargetKey(target.Method.MethodHandle);

			var annotationFile = annotationContext.TryGetDocument(targetKey);
			if (annotationFile == null)
			{
				var origin = TryGetAnnotationLocation(targetToAnnotate, messageLogger);
				if (origin == null)
				{
					annotationFile = annotationContext.GetUnknownOriginDocument();
				}
				else
				{
					annotationFile = annotationContext.TryGetDocument(origin);
					if (annotationFile == null)
					{
						var soureAnnotationFile = ParseAnnotationFile(target, origin, messageLogger);

						annotationFile = soureAnnotationFile;
						annotationContext.AddDocument(annotationFile);
						foreach (var benchmarkMethod in soureAnnotationFile.BenchmarkMethods.Keys)
						{
							var anotherKey = new AnnotationTargetKey(benchmarkMethod);
							if (!targetKey.Equals(anotherKey))
							{
								annotationContext.AddTargetKey(annotationFile, anotherKey);
							}
						}
					}
					else if (annotationFile is SourceAnnotationFile)
					{
						messageLogger.WriteSetupErrorMessage(
							target,
							$"The source file '{annotationFile.Origin}' does not contain code for method.");
						return;
					}
				}
				annotationContext.AddTargetKey(annotationFile, targetKey);
			}

			if (!annotationFile.Parsed)
			{
				messageLogger.WriteSetupErrorMessage(
					$"Could not find XML annotation file {annotationFile.Origin} for the benchmark. Annotations were not saved.");
				return;
			}

			var sourceAnnotationFile = (SourceAnnotationFile)annotationFile;
			messageLogger.Logger.WriteVerboseHint(
				$"Method {target.MethodDisplayInfo}: annotating file '{annotationFile.Origin}'");
			// TODO: log line???
			var annotated = TryUpdate(sourceAnnotationFile, targetToAnnotate);
			if (!annotated)
			{
				messageLogger.WriteSetupErrorMessage(
					target,
					$"Could not find annotations in source file '{sourceAnnotationFile.Origin}'.");
			}
			else
			{
				result.Add(targetToAnnotate);
				foreach (var metricValue in metrics)
				{
					messageLogger.Logger.WriteVerboseHint(
						$"Method {target.MethodDisplayInfo}: metric {metricValue.Metric} {metricValue} updated.");
				}
			}
		}

		#region Parse
		private static SourceAnnotationFile ParseAnnotationFile(Target target, string origin, IMessageLogger messageLogger)
		{
			var documentInfo = SymbolHelper.TryGetSourceInfo(target, messageLogger);
			if (documentInfo == null)
				return new SourceAnnotationFile(origin, Array<string>.Empty);

			Code.BugIf(documentInfo.Path != origin, "documentInfo.Path != origin");

			if (!TryValidate(documentInfo, messageLogger))
				return new SourceAnnotationFile(origin, Array<string>.Empty);

			var methodRanges = documentInfo.MethodLinesMap;
			var noCodeRanges = methodRanges.GetComplementation()
				.MakeInclusive(i => i, i => i)
				.TrimFrom(1);
			var candidateLines = methodRanges.SubRanges
				.Zip(
					noCodeRanges.SubRanges,
					(methodRange, noCodeRange) => noCodeRange.WithKey(methodRange.Key))
				.ToCompositeRange();
			Code.BugIf(
				candidateLines.SubRanges.Count != methodRanges.SubRanges.Count,
				"candidateLines.SubRanges.Count != map.SubRanges.Count");

			var sourceLines = TryReadFileContent(origin, messageLogger);
			if (sourceLines.Length == 0)
				return new SourceAnnotationFile(origin, sourceLines);

			var benchmarkMethods = FillAttributeLines(candidateLines, sourceLines, origin, messageLogger);
			return new SourceAnnotationFile(origin, sourceLines, benchmarkMethods);
		}
		private static bool TryValidate(SourceAnnotationInfo documentInfo, IMessageLogger messageLogger)
		{
			bool result = true;

			if (documentInfo.SourceLanguage != SourceLanguage.CSharp)
			{
				messageLogger.WriteSetupErrorMessage(
					$"Document language {documentInfo.SourceLanguage} is unsupported. File '{documentInfo.Path}'.");
				result = false;
			}

			if (documentInfo.MethodLinesMap.IsEmpty)
			{
				// TODO: improve message
				messageLogger.WriteSetupErrorMessage(
					$"No methods found in document '{documentInfo.Path}'.");
				result = false;
			}

			if (!documentInfo.MethodLinesMap.IsMerged)
			{
				var methodsIntersection = documentInfo.MethodLinesMap
					.GetIntersections()
					.FirstOrDefault(i => i.Ranges.Count > 1);
				DebugCode.BugIf(methodsIntersection.IsEmpty, "methodsIntersection.IsEmpty");

				var methodNames = methodsIntersection.Ranges.Select(r => r.Key.Name).Join(", ");

				// TODO: improve message
				messageLogger.WriteSetupErrorMessage(
					$"Some of methods in document share same source lines {methodsIntersection.IntersectionRange}: " +
						$"{methodNames}. Document '{documentInfo.Path}'.");
				result = false;
			}

			var symbolsChecksum = documentInfo.Checksum;
			var fileChecksum = AnnotationHelpers.TryGetChecksum(documentInfo.Path, documentInfo.ChecksumAlgorithm);

			if (!symbolsChecksum.EqualsTo(fileChecksum))
			{
				var expected = symbolsChecksum.ToHexString();
				var actual = fileChecksum.ToHexString();
				messageLogger.WriteSetupErrorMessage(
					$"{ChecksumAlgorithm.Sha1} checksum validation failed. File '{documentInfo.Path}'." +
						$"{Environment.NewLine}\tActual: 0x{actual}" +
						$"{Environment.NewLine}\tExpected: 0x{expected}");

				result = false;
			}

			return result;
		}

		[CanBeNull]
		private static string TryGetAnnotationLocation(
			[NotNull] CompetitionTarget targetToAnnotate,
			[NotNull] IMessageLogger messageLogger) =>
			SymbolHelper.TryGetSourcePath(targetToAnnotate.Target, messageLogger);

		[NotNull]
		private static string[] TryReadFileContent(string file, IMessageLogger messageLogger)
		{
			try
			{
				return IoHelpers.ReadFileContent(file);
			}
			catch (IOException ex)
			{
				messageLogger.WriteExceptionMessage(
					MessageSeverity.SetupError,
					$"Could not access file '{file}'.", ex);

				return Array<string>.Empty;
			}
			catch (UnauthorizedAccessException ex)
			{
				messageLogger.WriteExceptionMessage(
					MessageSeverity.SetupError,
					$"Could not access file '{file}'.", ex);

				return Array<string>.Empty;
			}
			catch (DecoderFallbackException ex)
			{
				messageLogger.WriteExceptionMessage(
					MessageSeverity.SetupError,
					$"Cannot detect encoding for file '{file}'. Try to save the file as UTF8 or UTF16.", ex);

				return Array<string>.Empty;
			}
		}
		#endregion


		private static bool TryUpdate(
			SourceAnnotationFile sourceCodeFile,
			CompetitionTarget competitionTarget)
		{
			if (!sourceCodeFile.Parsed)
				return false;

			var target = competitionTarget.Target;
			var targetKey = target.Method.MethodHandle;

			if (!sourceCodeFile.BenchmarkMethods.TryGetValue(
				targetKey,
				out var benchmarkMethod))
				return false;

			bool allFixed = true;

			var metricsByCategory = competitionTarget.MetricValues.GroupBy(m => m.Metric.Category);
			foreach (var metricGrouping in metricsByCategory.Select(x => x.ToArray()))
			{
				var metricAttributes = metricGrouping.Select(m => m.Metric.AttributeType.TypeHandle).ToHashSet();
				var attributeAppendLineNumber = benchmarkMethod.AttributeLineNumbers
					.Where(p => metricAttributes.Contains(p.Key))
					.MinOrDefault(p => p.Value, -1);
				var attributeInsertLineNumber = benchmarkMethod.AttributeLineNumbers
					.Where(p => metricAttributes.Contains(p.Key))
					.MaxOrDefault(p => p.Value, benchmarkMethod.PrimaryAttributeLineNumber);

				foreach (var metricValue in metricGrouping.Where(m => m.HasUnsavedChanges || m.ValuesRange.IsEmpty))
				{
					var attributeTypeHandle = metricValue.Metric.AttributeType.TypeHandle;
					if (benchmarkMethod.AttributeLineNumbers.TryGetValue(
						attributeTypeHandle,
						out var attributeLineNumber))
					{
						bool updated = TryUpdateLineWithAttribute(sourceCodeFile, attributeLineNumber, metricValue);

						if (updated && attributeAppendLineNumber <= 0)
						{
							attributeAppendLineNumber = attributeLineNumber;
						}
						allFixed &= updated;
					}
					else
					{
						bool inserted = false;
						if (metricValue.Metric.AnnotateInPlace && attributeAppendLineNumber > 0)
						{
							inserted = TryInsertAttributeInPlace(
								sourceCodeFile, attributeAppendLineNumber,
								benchmarkMethod, metricValue);
						}

						if (!inserted)
						{
							attributeInsertLineNumber = InsertLineWithAttribute(
								sourceCodeFile, attributeInsertLineNumber,
								benchmarkMethod, metricValue);
							if (attributeAppendLineNumber <= 0)
							{
								attributeAppendLineNumber = attributeInsertLineNumber;
							}
						}
					}
				}
			}

			return allFixed;
		}
	}
}